using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.AspNetCore;
using CellBridge.Lists.PostgreSql;
using Npgsql;

return await Benchmark.RunAsync(args);

internal static class Benchmark
{
    private static readonly ListActor Actor = new("benchmark:integration");
    private static readonly Guid Title = Guid.Parse("ba000000-0000-0000-0000-000000000001");
    private static readonly Guid Counter = Guid.Parse("ba000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("--clients 1,8,32 --workspaces 1,16 --iterations 200 --target-per-minute 0 --records 1000 --records-skip --modes provider,http --output artifacts/lists-performance/report.json");
            return 0;
        }
        Options options;
        try { options = Options.Parse(args); }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__lists");
        if (string.IsNullOrWhiteSpace(connection))
        { Console.Error.WriteLine("ConnectionStrings__lists must supply an administrative disposable PostgreSQL connection."); return 2; }
        var cases = new List<object>();
        object? preparation = null;
        object? metadata = null;
        string? fatal = null;
        var failed = false;
        var started = DateTimeOffset.UtcNow;
        using var listener = SqlMeasurements.Listen();
        try
        {
            await using var database = await BenchmarkDatabase.OpenAsync(connection);
            var engine = new PostgreSqlListEngine(database.Runtime);
            await engine.CheckHealthAsync();
            metadata = await MetadataAsync(database.Admin);
            await using var http = options.Modes.Contains("http") ? await BenchmarkHttpHost.StartAsync(engine, Actor) : null;
            foreach (var mode in options.Modes)
                foreach (var clients in options.Clients)
                    foreach (var workspaces in options.Workspaces)
                        foreach (var operation in new[] { "create", "patch" })
                        {
                            var result = await MutationCaseAsync(database, engine, http, mode, operation, clients, workspaces, options.Iterations, options.TargetPerMinute);
                            cases.Add(result.Report); failed |= result.Failed;
                        }
            if (!options.SkipRecords)
            {
                var list = await CreateListAsync(database, engine, "Query preparation");
                var clock = Stopwatch.StartNew();
                for (var i = 0; i < options.Records; i++)
                {
                    await engine.CreateRecordAsync(Actor, list.WorkspaceId, list.Id,
                        new(Guid.NewGuid(), list.SchemaVersion, Values("Prepared record", i)));
                    if ((i + 1) % 10000 == 0) Console.WriteLine($"Prepared {i + 1} query records through public creates.");
                }
                clock.Stop();
                preparation = new { skipped = false, records = options.Records, elapsedSeconds = clock.Elapsed.TotalSeconds,
                    creationPath = "IListEngine.CreateRecordAsync", measuredWorkload = false };
                var query = new ListQuery([new(Counter, FilterOperator.GreaterOrEqual, JsonValue.Create(options.Records / 2))],
                    Counter, Limit: Math.Min(100, options.Records / 4));
                foreach (var mode in options.Modes)
                    foreach (var page in new[] { "first-page", "second-page" })
                    {
                        var result = await QueryCaseAsync(engine, http, mode, list, query, page, options.Iterations, options.Records / 2);
                        cases.Add(result.Report); failed |= result.Failed;
                    }
            }
            else preparation = new { skipped = true, records = 0, measuredWorkload = false };
        }
        catch (Exception error)
        {
            // Connection strings and provider exception messages are deliberately excluded.
            fatal = ErrorKind(error); failed = true;
            Console.Error.WriteLine($"Benchmark stopped: {fatal}. Check operator credentials, database availability and the configured bounds.");
        }
        var report = new { startedUtc = started, completedUtc = DateTimeOffset.UtcNow,
            configuration = options, metadata, preparation, cases, fatalError = fatal,
            successful = !failed, memoryScope = "Whole process; HTTP includes loopback client and server.",
            byteMeasurement = "HTTP JSON bodies only; excludes headers and TCP. Provider body counters are zero.",
            sqlMeasurement = "Npgsql traced command/batch executions; statements within one batch are one execution. Commits use completed-transaction events and are counted separately. Warm-up and verification excluded." };
        var output = Path.GetFullPath(options.Output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, ReportJson));
        Console.WriteLine($"Benchmark report written to {output}");
        return failed ? 1 : 0;
    }

    private static async Task<CaseResult> MutationCaseAsync(BenchmarkDatabase database, PostgreSqlListEngine engine,
        BenchmarkHttpHost? http, string mode, string operation, int clients, int workspaceCount, int iterations, int targetPerMinute)
    {
        var preparation = Stopwatch.StartNew();
        var lists = new ListDefinition[workspaceCount];
        for (var i = 0; i < lists.Length; i++) lists[i] = await CreateListAsync(database, engine, $"{mode} {operation} {i}");
        var records = new WriterRecord?[clients, workspaceCount];
        if (operation == "patch")
            for (var writer = 0; writer < clients; writer++)
                for (var workspace = 0; workspace < lists.Length; workspace++)
                {
                    var list = lists[workspace];
                    var created = await engine.CreateRecordAsync(Actor, list.WorkspaceId, list.Id,
                        new(Guid.NewGuid(), list.SchemaVersion, Values("Independent patch record", -1)));
                    records[writer, workspace] = new(created.Id, created.Revision, -1);
                }
        // Every writer warms its connection/HTTP path on a separate record. Patch
        // measurement records still start at revision one.
        await Task.WhenAll(Enumerable.Range(0, clients).Select(async writer =>
        {
            var list = lists[writer % workspaceCount];
            var created = await engine.CreateRecordAsync(Actor, list.WorkspaceId, list.Id,
                new(Guid.NewGuid(), list.SchemaVersion, Values("Warm-up", -1)));
            await MutateAsync(engine, http, mode, operation, list, new(created.Id, created.Revision, -1), -2, new());
        }));
        preparation.Stop();
        var samples = new Sample[clients * iterations];
        var pacingIntervalMilliseconds = targetPerMinute == 0 ? 0 : clients * 60000d / targetPerMinute;
        var before = Resources.Take();
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, clients).Select(async writer =>
        {
            for (var i = 0; i < iterations; i++)
            {
                if (targetPerMinute > 0)
                    await WaitUntilAsync(clock, (i + writer / (double)clients) * pacingIntervalMilliseconds);
                var workspace = (writer + i) % workspaceCount;
                var metrics = new OperationMetrics();
                var timer = Stopwatch.StartNew();
                string? error = null;
                try { records[writer, workspace] = await MutateAsync(engine, http, mode, operation, lists[workspace], records[writer, workspace], i, metrics); }
                catch (Exception failure) { error = ErrorKind(failure); }
                timer.Stop();
                samples[writer * iterations + i] = new(timer.Elapsed.TotalMilliseconds, error, metrics);
            }
        }));
        if (targetPerMinute > 0) await WaitUntilAsync(clock, clients * iterations * 60000d / targetPerMinute);
        clock.Stop();
        var after = Resources.Take();
        var verificationErrors = 0;
        for (var writer = 0; writer < clients; writer++)
            for (var workspace = 0; workspace < workspaceCount; workspace++)
                if (records[writer, workspace] is { } expected)
                {
                    try
                    {
                        var list = lists[workspace];
                        var actual = await engine.GetRecordAsync(Actor, list.WorkspaceId, list.Id, expected.Id);
                        Require(actual.Revision == expected.Revision && actual.Values[Counter.ToString("D")]!.GetValue<long>() == expected.Counter,
                            "Persisted record verification failed.");
                    }
                    catch { verificationErrors++; }
                }
        var report = Summarize(mode, operation, clients, workspaceCount, iterations, samples, clock.Elapsed,
            before, after, preparation.Elapsed.TotalSeconds, verificationErrors, targetPerMinute);
        Console.WriteLine($"{mode} {operation}: {clients} clients / {workspaceCount} workspaces, {samples.Count(s => s.Error is null)}/{samples.Length} succeeded.");
        return new(report, verificationErrors != 0 || samples.Any(s => s.Error is not null));
    }

    private static async Task<WriterRecord> MutateAsync(PostgreSqlListEngine engine, BenchmarkHttpHost? http,
        string mode, string operation, ListDefinition list, WriterRecord? record, long counter, OperationMetrics metrics)
    {
        var path = $"/api/workspaces/{list.WorkspaceId:D}/lists/{list.Id:D}/records";
        if (operation == "create")
        {
            var request = new CreateRecordRequest(Guid.NewGuid(), list.SchemaVersion, Values("Measured create", counter));
            var result = mode == "http" ? await http!.SendAsync<CreateRecordResult>(path, HttpMethod.Post, request, metrics)
                : await ProviderAsync(() => engine.CreateRecordAsync(Actor, list.WorkspaceId, list.Id, request), metrics);
            Require(result.Id == request.Id && result.Revision == 1 && result.Number > 0 && !result.Replayed, "Create response verification failed.");
            Require(metrics.SqlExecutions > 0 && metrics.SqlCommits == 1, "SQL instrumentation did not observe the create and its commit.");
            return new(result.Id, result.Revision, counter);
        }
        Require(record is not null, "An independent patch record is required.");
        var patch = new UpdateRecordRequest(record!.Revision, list.SchemaVersion, [new(Counter, JsonValue.Create(counter))]);
        var updated = mode == "http" ? await http!.SendAsync<ListRecord>($"{path}/{record.Id:D}", HttpMethod.Patch, patch, metrics)
            : await ProviderAsync(() => engine.UpdateRecordAsync(Actor, list.WorkspaceId, list.Id, record.Id, patch), metrics);
        Require(updated.Id == record.Id && updated.WorkspaceId == list.WorkspaceId && updated.ListId == list.Id
            && updated.Revision == record.Revision + 1 && updated.Values[Counter.ToString("D")]!.GetValue<long>() == counter,
            "Patch response verification failed.");
        Require(metrics.SqlExecutions > 0 && metrics.SqlCommits == 1, "SQL instrumentation did not observe the patch and its commit.");
        return new(updated.Id, updated.Revision, counter);
    }

    private static async Task<CaseResult> QueryCaseAsync(PostgreSqlListEngine engine, BenchmarkHttpHost? http,
        string mode, ListDefinition list, ListQuery query, string page, int iterations, int firstCounter)
    {
        var path = $"/api/workspaces/{list.WorkspaceId:D}/lists/{list.Id:D}/records/query";
        Guid? nextId = null;
        string? cursor = null;
        if (mode == "http")
        {
            var first = await http!.SendAsync<ListQueryResponse>(path, HttpMethod.Post, new ListQueryRequest(query), new());
            cursor = first.NextCursor;
            Require(cursor is not null, "Preparation must contain a second query page.");
        }
        else
        {
            var first = await engine.QueryAsync(Actor, list.WorkspaceId, list.Id, query);
            nextId = first.NextRecordId;
            Require(nextId is not null, "Preparation must contain a second query page.");
        }
        var samples = new Sample[iterations];
        var before = Resources.Take();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var metrics = new OperationMetrics();
            var timer = Stopwatch.StartNew();
            string? error = null;
            try
            {
                IReadOnlyList<ListRecord> records;
                if (mode == "http") records = (await http!.SendAsync<ListQueryResponse>(path, HttpMethod.Post,
                    new ListQueryRequest(query, page == "second-page" ? cursor : null), metrics)).Records;
                else records = (await ProviderAsync(() => engine.QueryAsync(Actor, list.WorkspaceId, list.Id,
                    query with { AfterRecordId = page == "second-page" ? nextId : null }), metrics)).Records;
                Require(records.Count == query.Limit, "Query must return exactly one bounded page.");
                var start = firstCounter + (page == "second-page" ? query.Limit : 0);
                for (var j = 0; j < records.Count; j++)
                    Require(records[j].WorkspaceId == list.WorkspaceId && records[j].ListId == list.Id
                        && records[j].Values[Counter.ToString("D")]!.GetValue<long>() == start + j, "Query order/value verification failed.");
            }
            catch (Exception failure) { error = ErrorKind(failure); }
            timer.Stop(); samples[i] = new(timer.Elapsed.TotalMilliseconds, error, metrics);
        }
        clock.Stop();
        var report = Summarize(mode, $"query-{page}", 1, 1, iterations, samples, clock.Elapsed, before,
            Resources.Take(), 0, 0);
        Console.WriteLine($"{mode} query {page}: {samples.Count(s => s.Error is null)}/{iterations} succeeded.");
        return new(report, samples.Any(s => s.Error is not null));
    }

    private static async Task<T> ProviderAsync<T>(Func<Task<T>> operation, OperationMetrics metrics)
    {
        using var measurement = SqlMeasurements.Begin();
        try { return await operation(); }
        finally { metrics.SqlExecutions = measurement.Executions; metrics.SqlCommits = measurement.Commits; }
    }

    private static async Task<ListDefinition> CreateListAsync(BenchmarkDatabase database, IListEngine engine, string name)
    {
        var workspace = Guid.NewGuid();
        await PostgreSqlListSetup.RegisterWorkspaceAsync(database.Admin, workspace, name, Actor.Subject);
        return await engine.CreateListAsync(Actor, workspace,
            new(Guid.NewGuid(), name, [new(Title, "Title", ListFieldType.Text, Required: true), new(Counter, "Counter", ListFieldType.Integer, Required: true)]));
    }
    private static JsonObject Values(string title, long counter) => new() { [Title.ToString("D")] = title, [Counter.ToString("D")] = counter };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string ErrorKind(Exception error) => error switch
    {
        ListException list => "list:" + list.Failure,
        HttpRequestException http when http.StatusCode is { } status => "http:" + (int)status,
        PostgresException postgres => "postgres:" + postgres.SqlState,
        _ => error.GetType().Name
    };

    private static object Summarize(string mode, string operation, int clients, int workspaces, int iterations,
        Sample[] samples, TimeSpan elapsed, Resources before, Resources after, double preparationSeconds, int verificationErrors, int targetPerMinute = 0)
    {
        var success = samples.Where(s => s.Error is null).ToArray();
        return new { mode, operation, clients, workspaces, activeWorkspaces = Math.Min(workspaces, clients + iterations - 1), iterationsPerClient = iterations,
            attempted = samples.Length, succeeded = success.Length, errors = samples.Length - success.Length, verificationErrors,
            errorKinds = samples.Where(s => s.Error is not null).GroupBy(s => s.Error!).ToDictionary(g => g.Key, g => g.Count()),
            elapsedSeconds = elapsed.TotalSeconds, successfulOperationsPerSecond = success.Length / elapsed.TotalSeconds,
            preparationSeconds, targetOperationsPerMinute = targetPerMinute,
            offeredOperationsPerMinute = samples.Length * 60 / elapsed.TotalSeconds,
            pacingIntervalMillisecondsPerWriter = targetPerMinute == 0 ? (double?)null : clients * 60000d / targetPerMinute,
            pacing = targetPerMinute == 0 ? "Saturation" : "Per-writer absolute deadlines with staggered starts; elapsed includes the complete pacing interval.",
            latencyMillisecondsSuccessful = Percentiles(success.Select(s => s.Milliseconds)),
            latencyMillisecondsAllAttempts = Percentiles(samples.Select(s => s.Milliseconds)),
            sqlExecutions = samples.Sum(s => (long)s.Metrics.SqlExecutions), sqlCommits = samples.Sum(s => (long)s.Metrics.SqlCommits),
            successfulSqlExecutionsPerOperation = Percentiles(success.Select(s => (double)s.Metrics.SqlExecutions)),
            successfulSqlCommitsPerOperation = Percentiles(success.Select(s => (double)s.Metrics.SqlCommits)),
            sqlExecutionDistribution = samples.GroupBy(s => s.Metrics.SqlExecutions).ToDictionary(g => g.Key, g => g.Count()),
            requestBodyBytes = samples.Sum(s => s.Metrics.RequestBodyBytes), responseBodyBytes = samples.Sum(s => s.Metrics.ResponseBodyBytes),
            allocatedBytes = after.AllocatedBytes - before.AllocatedBytes, gcCollections = new[] { after.Gen0 - before.Gen0, after.Gen1 - before.Gen1, after.Gen2 - before.Gen2 },
            cpuMilliseconds = after.CpuMilliseconds - before.CpuMilliseconds,
            workingSetBeforeBytes = before.WorkingSetBytes, workingSetAfterBytes = after.WorkingSetBytes,
            privateMemoryBeforeBytes = before.PrivateMemoryBytes, privateMemoryAfterBytes = after.PrivateMemoryBytes,
            processPeakWorkingSetBytes = after.PeakWorkingSetBytes };
    }
    private static object Percentiles(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        double? P(double percentile) => sorted.Length == 0 ? null : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1)];
        return new { p50 = P(.50), p95 = P(.95), p99 = P(.99), mean = sorted.Length == 0 ? (double?)null : sorted.Average() };
    }
    private static async Task<object> MetadataAsync(NpgsqlDataSource admin)
    {
        var settings = new Dictionary<string, string>();
        await using (var command = admin.CreateCommand("SELECT name,setting FROM pg_settings WHERE name IN ('synchronous_commit','fsync','full_page_writes','wal_level','shared_buffers','max_connections','statement_timeout','checkpoint_timeout','max_wal_size') ORDER BY name"))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) settings.Add(reader.GetString(0), reader.GetString(1));
        await using var version = admin.CreateCommand("SELECT version()");
        return new { machine = Environment.MachineName, cpu = CpuDescription(), processors = Environment.ProcessorCount,
            framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            configuredCpuSet = Environment.GetEnvironmentVariable("CELLBRIDGE_BENCHMARK_CPU_SET"),
            configuredPostgresMemory = Environment.GetEnvironmentVariable("CELLBRIDGE_BENCHMARK_POSTGRES_MEMORY"),
            availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            source = SourceInformation(),
            postgresVersion = (string)(await version.ExecuteScalarAsync())!, postgresSettings = settings,
            runtimePrivileges = "Dedicated NOSUPERUSER NOBYPASSRLS NOINHERIT login; provider health check passed.",
            providerTransactionSettings = new { synchronousCommit = "on", statementTimeoutMilliseconds = 10000, lockTimeoutMilliseconds = 10000 },
            quotas = new { maxRecordsPerList = (long?)null, maxWorkspaceBytes = (long?)null, maxAuditEvents = (long?)null } };
    }

    private static async Task WaitUntilAsync(Stopwatch clock, double deadlineMilliseconds)
    {
        double remaining;
        while ((remaining = deadlineMilliseconds - clock.Elapsed.TotalMilliseconds) > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, remaining)));
    }

    private static string CpuDescription()
    {
        if (File.Exists("/proc/cpuinfo"))
        {
            var model = File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal));
            if (model is not null) return model.Split(':', 2)[1].Trim();
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static object SourceInformation()
    {
        string? Git(params string[] arguments)
        {
            try
            {
                var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd();
                _ = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0 ? output.Trim() : null;
            }
            catch { return null; }
        }
        var status = Git("status", "--porcelain", "--untracked-files=normal");
        var assemblies = new[] { typeof(Benchmark).Assembly, typeof(IListEngine).Assembly,
            typeof(PostgreSqlListEngine).Assembly, typeof(CellBridgeListsOptions).Assembly };
        return new { commit = Git("rev-parse", "HEAD"), workingTreeDirty = status is null ? (bool?)null : status.Length != 0,
            assemblySha256 = assemblies.ToDictionary(assembly => assembly.GetName().Name!,
                assembly => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location)))) };
    }

    private sealed record WriterRecord(Guid Id, long Revision, long Counter);
    private sealed record Sample(double Milliseconds, string? Error, OperationMetrics Metrics);
    private sealed record CaseResult(object Report, bool Failed);
    private sealed record Resources(long AllocatedBytes, int Gen0, int Gen1, int Gen2, double CpuMilliseconds,
        long WorkingSetBytes, long PrivateMemoryBytes, long PeakWorkingSetBytes)
    {
        public static Resources Take()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return new(GC.GetTotalAllocatedBytes(false), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64, process.PrivateMemorySize64, process.PeakWorkingSet64);
        }
    }
    private sealed record Options(int[] Clients, int[] Workspaces, int Iterations, int TargetPerMinute, int Records, bool SkipRecords, string[] Modes, string Output)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>();
            var skip = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--records-skip") { skip = true; continue; }
                if (!new[] { "--clients", "--workspaces", "--iterations", "--target-per-minute", "--records", "--modes", "--output" }.Contains(args[i]) || i + 1 == args.Length)
                    throw new ArgumentException("Unknown argument or missing value. Run with --help.");
                if (!values.TryAdd(args[i], args[++i])) throw new ArgumentException("Duplicate benchmark argument.");
            }
            string Value(string key, string fallback) => values.GetValueOrDefault(key, fallback);
            static int Number(string value, int min, int max) => int.TryParse(value, out var number) && number >= min && number <= max
                ? number : throw new ArgumentException($"Benchmark integers must be between {min} and {max}.");
            var clients = Value("--clients", "1,8,32").Split(',').Select(v => Number(v, 1, 128)).Distinct().ToArray();
            var workspaces = Value("--workspaces", "1,16").Split(',').Select(v => Number(v, 1, 64)).Distinct().ToArray();
            var modes = Value("--modes", "provider,http").Split(',').Distinct().ToArray();
            if (modes.Any(m => m is not ("provider" or "http"))) throw new ArgumentException("Modes must be provider and/or http.");
            return new(clients, workspaces, Number(Value("--iterations", "200"), 1, 10000),
                Number(Value("--target-per-minute", "0"), 0, 10000000),
                Number(Value("--records", "1000"), 4, 1000000), skip, modes, Value("--output", "artifacts/lists-performance/report.json"));
        }
    }
}
