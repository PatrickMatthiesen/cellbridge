using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CellBridge.TestInfrastructure;
using Npgsql;

internal sealed partial class DocumentLoadScenario
{
    private JsonObject? userDatabaseBefore;
    private OwnedWebDemoProcessProbe? userProcessProbe;
    private WebDemoProcessSampler? userProcessSampler;
    private string? userProcessFailure;
    private readonly object userDatabaseSamplingGate = new();
    private DatabaseActivitySampler? userDatabaseSampler;

    private void StartUserDatabaseSampling()
    {
        lock (userDatabaseSamplingGate)
        {
            if (userProcessSampler is null && userProcessProbe is not null)
                userProcessSampler = new(userProcessProbe.ResourceProcessId, userProcessProbe.Read,
                    userMeasurementStart, userMeasurementStart + userMeasurementMilliseconds);
            if (userDatabaseBefore is null || userDatabaseSampler is not null) return;
            NpgsqlConnection? connection = null;
            userDatabaseSampler = new DatabaseActivitySampler(async token =>
            {
                connection ??= await OpenStatisticsConnectionAsync(await resources.EnvironmentAsync(), token);
                await using var command = new NpgsqlCommand("""
                    SELECT COALESCE(jsonb_agg(to_jsonb(s)), '[]'::jsonb)::text FROM (
                        SELECT state, wait_event_type, wait_event,
                            CASE
                                WHEN query ILIKE '%cellbridge_repository_%' THEN 'normalized-document'
                                WHEN query ILIKE '%cellbridge_objects%' OR query ILIKE '%cellbridge_chunks%' THEN 'physical-content'
                                WHEN query ILIKE '%cellbridge_recovery_%' THEN 'recovery'
                                WHEN query ILIKE '%AspNet%' THEN 'identity'
                                ELSE 'other'
                            END AS query_class,
                            count(*) AS backends,
                            count(*) FILTER (WHERE cardinality(pg_blocking_pids(pid)) > 0) AS blocked_backends,
                            sum(cardinality(pg_blocking_pids(pid))) AS blocking_pids
                        FROM pg_stat_activity
                        WHERE datid=(SELECT oid FROM pg_database WHERE datname=current_database())
                            AND backend_type='client backend' AND pid <> pg_backend_pid()
                        GROUP BY state,wait_event_type,wait_event,query_class
                    ) s
                    """, connection) { CommandTimeout = 5 };
                var json = await command.ExecuteScalarAsync(token) as string
                    ?? throw new InvalidDataException("Missing activity sample.");
                return JsonNode.Parse(json)?.AsArray() ?? throw new InvalidDataException("Invalid activity sample.");
            }, async () => { if (connection is not null) await connection.DisposeAsync(); }, resources.CancellationToken);
        }
    }

    private async Task StopUserDatabaseSamplingAsync()
    {
        DatabaseActivitySampler? sampler;
        lock (userDatabaseSamplingGate) sampler = userDatabaseSampler;
        if (sampler is not null) await sampler.StopAsync();
        if (userProcessSampler is not null) await userProcessSampler.StopAsync();
    }

    private void IdentifyUserApplicationProcess(LoadTestEnvironment environment)
    {
        try
        {
            userProcessProbe = environment.CreateWebDemoProcessProbe();
            Console.WriteLine($"WebDemo metrics process verified: resource-pid={userProcessProbe.ResourceProcessId}, " +
                $"application-pid={userProcessProbe.ProcessId}, application-start-utc={userProcessProbe.StartUtc:O}.");
        }
        catch (Exception exception)
        {
            userProcessFailure = exception.GetType().Name;
            Console.WriteLine($"WebDemo process metrics unavailable: {userProcessFailure}. No unrelated process will be sampled.");
        }
    }

    private async Task ResetUserDatabaseStatisticsAsync()
    {
        var environment = await resources.EnvironmentAsync();
        if (!settings.UserModel) return;
        if (environment.Backend != "postgresql" || !environment.QueryStatisticsEnabled) return;
        await using var connection = await OpenStatisticsConnectionAsync(environment, resources.CancellationToken);
        // Only statement statistics for the owned load database are reset. Cluster I/O
        // and WAL counters retain their baseline so setup work can be subtracted.
        await using (var reset = new NpgsqlCommand("""
            SELECT pg_stat_statements_reset(0, (SELECT oid FROM pg_database WHERE datname=current_database()), 0)
            """, connection))
            await reset.ExecuteNonQueryAsync(resources.CancellationToken);
        userDatabaseBefore = await ReadDatabaseCountersAsync(connection, resources.CancellationToken);
    }

    private async Task CaptureUserDatabaseStatisticsAsync()
    {
        await StopUserDatabaseSamplingAsync();
        if (userDatabaseBefore is null)
        {
            var processDirectory = Path.Combine(CellBridgeLoadTests.RepositoryRoot(), "artifacts", "load-tests");
            Directory.CreateDirectory(processDirectory);
            var processPath = Path.Combine(processDirectory, $"office-users-{settings.Clients}-{Guid.NewGuid():N}.process.json");
            await File.WriteAllTextAsync(processPath, new JsonObject
            {
                ["workload"] = UserMeasurementReport(),
                ["webDemoProcessMetrics"] = ProcessReport(),
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"WebDemo process statistics: report={processPath}");
            return;
        }
        var environment = await resources.EnvironmentAsync();
        // Preserve requested diagnostics even when a load failure cancelled its requests.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = await OpenStatisticsConnectionAsync(environment, timeout.Token);
        var after = await ReadDatabaseCountersAsync(connection, timeout.Token);
        var statements = await ReadStatisticsJsonAsync(connection, """
            SELECT COALESCE(jsonb_agg(to_jsonb(s) ORDER BY s.total_exec_time DESC), '[]'::jsonb)::text
            FROM (
                SELECT queryid::text AS query_id, calls, total_exec_time, mean_exec_time, rows,
                    shared_blks_hit, shared_blks_read, shared_blks_written, shared_blks_dirtied,
                    shared_blk_read_time, shared_blk_write_time, wal_bytes, query
                FROM pg_stat_statements
                WHERE dbid=(SELECT oid FROM pg_database WHERE datname=current_database())
                ORDER BY total_exec_time DESC LIMIT 30
            ) s
            """, timeout.Token) as JsonArray ?? throw new InvalidDataException("Missing PostgreSQL statement report.");
        foreach (var statement in statements.OfType<JsonObject>())
            statement["query"] = NormalizeReportedSql(statement["query"]!.GetValue<string>());

        var ioDeltas = new JsonArray();
        var beforeIo = userDatabaseBefore["io"]!.AsArray().OfType<JsonObject>().ToDictionary(IoKey);
        foreach (var row in after["io"]!.AsArray().OfType<JsonObject>())
        {
            beforeIo.TryGetValue(IoKey(row), out var before);
            var delta = CounterDelta(before, row);
            foreach (var key in new[] { "backend_type", "object", "context" }) delta[key] = row[key]!.DeepClone();
            ioDeltas.Add(delta);
        }
        var walDelta = CounterDelta(userDatabaseBefore["wal"]!.AsObject(), after["wal"]!.AsObject());
        var databaseDelta = CounterDelta(userDatabaseBefore["database"]!.AsObject(), after["database"]!.AsObject(),
            "datid", "numbackends");
        var report = new JsonObject
        {
            ["scope"] = "Warm-up plus measurement and completion drain, before final validation. PostgreSQL cumulative counters may lag activity.",
            ["ioScope"] = "pg_stat_io and pg_stat_wal cover the disposable cluster, including authentication, health checks and background activity. Database counters and statements cover cellbridge.",
            ["inventoryScope"] = "Document charges include metadata and references per document; shared physical objects are counted once separately. Table bytes include indexes and allocated free space. Neither is a provider quota.",
            ["queryText"] = "pg_stat_statements normalized SQL, with remaining literals/comments removed and text limited to 1024 characters.",
            ["timing"] = "Statement execution and I/O times are milliseconds. Query collection is enabled throughout load; report queries run outside HTTP timing.",
            ["users"] = settings.Clients,
            ["documentsPerUser"] = settings.DocumentsPerClient,
            ["protocolConcurrency"] = environment.ProtocolConcurrency,
            ["protocolConcurrencyPerSubject"] = environment.ProtocolConcurrencyPerSubject,
            ["configuredWebDemoPostgreSqlMaximumPoolSizePerPool"] = environment.PostgreSqlPoolSize,
            ["poolScope"] = "When configured, the limit applies separately to WebDemo document and Identity pools. Activity samples are server connections, not a client pool high-water mark.",
            ["configuredWebDemoPostgreSqlMaxAutoPreparePerConnection"] = environment.PostgreSqlMaxAutoPrepare,
            ["effectiveWebDemoPostgreSqlMaxAutoPreparePerConnection"] = environment.PostgreSqlMaxAutoPrepare ?? new NpgsqlConnectionStringBuilder().MaxAutoPrepare,
            ["webDemoPostgreSqlAutoPrepareMinUsages"] = new NpgsqlConnectionStringBuilder().AutoPrepareMinUsages,
            ["autoPrepareScope"] = "Benchmark-only WebDemo connection-string override. The cap is per physical connection in each document/Identity pool; runner and diagnostic connections retain their existing settings. The driver default minimum usages is unchanged.",
            ["before"] = userDatabaseBefore.DeepClone(),
            ["after"] = after,
            ["ioDelta"] = ioDeltas,
            ["walDelta"] = walDelta,
            ["databaseDelta"] = databaseDelta,
            ["topStatementsByTotalExecutionTime"] = statements,
            ["activitySampling"] = userDatabaseSampler?.Report(),
            ["webDemoProcessMetrics"] = ProcessReport(),
            ["workload"] = UserMeasurementReport(),
        };
        var directory = Path.Combine(CellBridgeLoadTests.RepositoryRoot(), "artifacts", "load-tests");
        Directory.CreateDirectory(directory);
        var filename = $"office-users-{settings.Clients}-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.pgstats.json";
        var path = Path.Combine(directory, filename);
        await File.WriteAllTextAsync(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
        Console.WriteLine($"PostgreSQL statistics including warm-up: database-blocks-read={databaseDelta["blks_read"]}, " +
            $"database-block-read-ms={databaseDelta["blk_read_time"]}, WAL-bytes={walDelta["wal_bytes"]}; report={path}");
        foreach (var statement in statements.OfType<JsonObject>().Take(5))
        {
            var query = statement["query"]!.GetValue<string>();
            Console.WriteLine($"PostgreSQL query {statement["query_id"]}: calls={statement["calls"]}, " +
                $"total-ms={statement["total_exec_time"]}, mean-ms={statement["mean_exec_time"]}, " +
                $"shared-blocks-read={statement["shared_blks_read"]}; {query[..Math.Min(160, query.Length)]}");
        }
        userDatabaseBefore = null;
    }

    private JsonObject UserMeasurementReport() => new()
    {
        ["authenticatedUsers"] = settings.Clients,
        ["startedUsers"] = userSessions.Count(user => user.Started),
        ["documentsPerUser"] = settings.DocumentsPerClient,
        ["documents"] = httpDocuments.Count,
        ["measurementMilliseconds"] = userMeasurementMilliseconds,
        ["saveStartsInWindow"] = userMeasuredSaveStarts,
        ["acknowledgedCommitsInWindow"] = userMeasuredCommits,
        ["lateAcknowledgementsExcluded"] = userMeasuredLateAcknowledgements,
        ["acknowledgedCommitsPerMinute"] = userMeasurementMilliseconds > 0 ? userMeasuredCommits * 60_000d / userMeasurementMilliseconds : null,
        ["offeredSavesPerMinute"] = settings.Clients * 60_000d / settings.SaveIntervalMs,
        ["missedCadenceSlotsIncludingWarmup"] = userMissedCadences,
        ["maximumActionStartLatenessMillisecondsIncludingWarmup"] = userMaximumLateStartMilliseconds,
        ["contentVerification"] = "Capture precedes final package and receipt verification; successful completion also requires that verification.",
    };

    private JsonObject ProcessReport() => userProcessSampler?.Report() ?? new JsonObject
    {
        ["status"] = "notCollected",
        ["failureType"] = userProcessFailure,
        ["reason"] = "Could not verify an owned WebDemo application process from the Aspire resource snapshot.",
        ["gc"] = "notCollected",
    };

    private static async Task<NpgsqlConnection> OpenStatisticsConnectionAsync(LoadTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(environment.PostgreSqlDocumentConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            // Diagnostic queries must not become entries in their own workload report.
            await using var command = new NpgsqlCommand("SET pg_stat_statements.track='none'", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    // PostgreSQL 18 view columns and units:
    // https://www.postgresql.org/docs/18/monitoring-stats.html
    // https://www.postgresql.org/docs/18/pgstatstatements.html
    private static async Task<JsonObject> ReadDatabaseCountersAsync(NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var counters = (await ReadStatisticsJsonAsync(connection, """
            SELECT jsonb_build_object(
                'capturedUtc', clock_timestamp(),
                'io', (SELECT jsonb_agg(to_jsonb(i) ORDER BY backend_type,object,context) FROM pg_stat_io i),
                'wal', (SELECT to_jsonb(w) FROM pg_stat_wal w),
                'database', (SELECT to_jsonb(d) FROM pg_stat_database d WHERE datname=current_database()),
                'settings', (SELECT jsonb_object_agg(name,setting) FROM pg_settings
                    WHERE name IN ('server_version','server_version_num','max_connections','reserved_connections',
                        'superuser_reserved_connections','fsync','synchronous_commit','full_page_writes',
                        'max_wal_size','shared_buffers','block_size','track_io_timing','track_wal_io_timing','shared_preload_libraries')),
                'connectionActivity', (SELECT jsonb_agg(to_jsonb(a)) FROM (
                    SELECT datname, state, wait_event_type, count(*) AS connections
                    FROM pg_stat_activity WHERE backend_type='client backend'
                    GROUP BY datname,state,wait_event_type ORDER BY datname,state,wait_event_type
                ) a),
                'tableEstimates', (SELECT jsonb_agg(to_jsonb(t)) FROM (
                    SELECT relname, n_live_tup, n_dead_tup, seq_scan, idx_scan, n_tup_ins, n_tup_upd,
                        pg_total_relation_size(relid) AS relation_bytes
                    FROM pg_stat_user_tables ORDER BY relname
                ) t),
                'inventory', jsonb_build_object(
                    'documentCount', (SELECT count(*) FROM cellbridge_repository_heads WHERE NOT "IsDeleted"),
                    'documentChargedBytes', (SELECT COALESCE(SUM(("Json"::jsonb->'Counters'->>'StoredBytes')::bigint),0)
                        FROM cellbridge_repository_heads),
                    'physicalObjectBytes', (SELECT COALESCE(SUM(length),0) FROM cellbridge_objects),
                    'normalizedTableBytes', (SELECT COALESCE(SUM(pg_total_relation_size(relid)),0)
                        FROM pg_stat_user_tables WHERE starts_with(relname,'cellbridge_repository_')))
            )::text
            """, cancellationToken)).AsObject();
        return counters;
    }

    private static async Task<JsonNode> ReadStatisticsJsonAsync(NpgsqlConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidDataException("PostgreSQL returned no statistics JSON.");
        return JsonNode.Parse(json) ?? throw new InvalidDataException("Invalid PostgreSQL statistics JSON.");
    }

    private static string IoKey(JsonObject row) => $"{row["backend_type"]}/{row["object"]}/{row["context"]}";

    private static JsonObject CounterDelta(JsonObject? before, JsonObject after, params string[] gauges)
    {
        var reset = before is null || before["stats_reset"]?.ToJsonString() != after["stats_reset"]?.ToJsonString();
        var delta = new JsonObject { ["resetDetected"] = reset };
        foreach (var (name, value) in after)
        {
            if (gauges.Contains(name) || value is not JsonValue number || !number.TryGetValue<decimal>(out var current)) continue;
            decimal? difference = null;
            if (!reset && before![name] is JsonValue old && old.TryGetValue<decimal>(out var previous) && current >= previous)
                difference = current - previous;
            delta[name] = difference;
        }
        return delta;
    }

    private static string NormalizeReportedSql(string sql)
    {
        var timeout = TimeSpan.FromSeconds(1);
        sql = Regex.Replace(sql, @"\$(?<tag>[A-Za-z_][A-Za-z0-9_]*|)\$[\s\S]*?\$\k<tag>\$", "?", RegexOptions.None, timeout);
        sql = Regex.Replace(sql, @"'(?:''|[^'])*'", "?", RegexOptions.None, timeout);
        sql = Regex.Replace(sql, @"--[^\r\n]*|/\*[\s\S]*?\*/", " ", RegexOptions.None, timeout);
        sql = Regex.Replace(sql, @"(?<![\w$])\d+(?:\.\d+)?(?!\w)", "?", RegexOptions.None, timeout);
        sql = Regex.Replace(sql, @"\s+", " ", RegexOptions.None, timeout).Trim();
        return sql[..Math.Min(sql.Length, 1024)];
    }
}
