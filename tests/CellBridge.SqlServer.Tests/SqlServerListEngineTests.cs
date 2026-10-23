using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

// Each test uses a separate logical database in the existing native Aspire fixture.
// No extra engine/container, shared fixture edits or document-provider dependencies.
[Collection("SQL Server")]
public sealed class SqlServerListEngineTests(SqlServerFixture fixture)
{
    private static readonly ListActor Admin = new("administrator");

    [Fact]
    public async Task RestrictedRuntimeCreatesReadsAndRetriesWithoutDdlOrExtraCharges()
    {
        var env = await SetupAsync();
        await env.Engine.CheckHealthAsync();
        await Failure(ListFailure.Unavailable, () => new SqlServerListEngine(env.AdminOptions).CheckHealthAsync());
        await using (var db = new SqlServerListDbContext(env.RuntimeOptions))
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("CREATE TABLE cellbridge_lists.forbidden(id int)"));
        var list = await env.CreateListAsync();
        var request = Request(list);
        var before = await env.SnapshotAsync(list.Id);
        var result = await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        var after = await env.SnapshotAsync(list.Id);
        var record = await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, request.Id);
        Assert.Equal(1, result.Number);
        Assert.Equal(1, result.Revision);
        Assert.False(result.Replayed);
        Assert.Equal(request.Values.ToJsonString(), record.Values.ToJsonString());
        Assert.Equal(record.CreatedUtc, record.ModifiedUtc);
        Assert.Equal(Admin.Subject, record.CreatedBy);
        var reloaded = await env.Engine.GetListAsync(Admin, env.Workspace, list.Id);
        Assert.Equal(list.Id, reloaded.Id);
        Assert.Equal(list.Fields, reloaded.Fields);
        Assert.Single(await env.Engine.GetListsAsync(Admin, env.Workspace));
        var replay = await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        Assert.Equal(result with { Replayed = true }, replay);
        Assert.Equal(after, await env.SnapshotAsync(list.Id));
        Assert.Equal(before.Audits + 1, after.Audits);
        Assert.Equal(before.Identities + 1, after.Identities);
        Assert.Equal(before.NextNumber + 1, after.NextNumber);
        var expectedDelta = Encoding.UTF8.GetByteCount(request.Values.ToJsonString()) + 192L +
            Encoding.UTF8.GetByteCount(ListValidation.CreateDigest(request)) + Encoding.UTF8.GetByteCount(Admin.Subject) +
            128 + 96 + Encoding.UTF8.GetByteCount(Admin.Subject) + Encoding.UTF8.GetByteCount("record.create") + 16;
        Assert.Equal(expectedDelta, after.Bytes - before.Bytes);
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, env.Workspace))
        {
            var receipt = await scope.Context.CreateReceipts.SingleAsync();
            Assert.Equal(record.CreatedUtc, receipt.CreatedUtc);
            Assert.Equal(record.CreatedUtc.AddDays(7), receipt.ExpiresUtc);
            var audit = await scope.Context.AuditEvents.SingleAsync(row => row.Action == "record.create");
            Assert.Equal(record.CreatedUtc, audit.Utc);
            Assert.Equal(request.Id, audit.RecordId);
        }
        Assert.Equal(0, await UnscopedCountAsync(env.Runtime));
    }

    [Fact]
    public async Task MembershipAndIndependentFlagsAndCurrentAccessPrecedeReceipts()
    {
        var receiptReads = new ObserveReceiptReads();
        var env = await SetupAsync(receiptReads);
        var list = await env.CreateListAsync();
        var request = Request(list);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        var baseline = await env.SnapshotAsync(list.Id);
        await env.SetMemberAsync("manager", create: false, manage: true);
        await env.SetMemberAsync("creator", create: true, manage: false);
        await env.SetMemberAsync("writer", create: false, manage: false);
        await env.SetMemberAsync("reader", create: false, manage: false);
        await env.SetGrantAsync(list.Id, "writer", ListAccess.Write);
        await env.SetGrantAsync(list.Id, "reader", ListAccess.Read);
        await env.SetGrantAsync(list.Id, "manager", ListAccess.Design);
        await Failure(ListFailure.Forbidden, () => env.Engine.CreateListAsync(new("manager"), env.Workspace,
            NewListRequest()));
        var created = await env.Engine.CreateListAsync(new("creator"), env.Workspace, NewListRequest());
        Assert.Equal("Rows", created.Name);
        await Failure(ListFailure.NotSupported, () => env.Engine.SetListGrantAsync(new("creator"), env.Workspace,
            list.Id, "reader", ListAccess.Write));
        await Failure(ListFailure.NotFound, () => env.Engine.GetRecordAsync(new("writer"), env.Workspace, list.Id, request.Id));
        Assert.Equal(request.Id, (await env.Engine.GetRecordAsync(new("reader"), env.Workspace, list.Id, request.Id)).Id);
        receiptReads.Count = 0;
        await Failure(ListFailure.NotFound, () => env.Engine.CreateRecordAsync(new("reader"), env.Workspace, list.Id, request));
        await Failure(ListFailure.Forbidden, () => env.Engine.CreateRecordAsync(new("missing"), env.Workspace, list.Id, request));
        Assert.Equal(0, receiptReads.Count);
        await Failure(ListFailure.Conflict, () => env.Engine.CreateRecordAsync(new("writer"), env.Workspace, list.Id, request));
        Assert.True(receiptReads.Count > 0);
        await env.SetGrantAsync(list.Id, Admin.Subject, ListAccess.None);
        receiptReads.Count = 0;
        await Failure(ListFailure.NotFound, () => env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request));
        Assert.Equal(0, receiptReads.Count);
        // Membership revocation also rejects a previously authorized retry.
        await env.SetMemberAsync(Admin.Subject, remove: true);
        receiptReads.Count = 0;
        await Failure(ListFailure.Forbidden, () => env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request));
        Assert.Equal(0, receiptReads.Count);
        Assert.Equal(baseline.Records, (await env.SnapshotAsync(list.Id)).Records);
        var otherWorkspace = Guid.NewGuid();
        await SqlServerListSetup.RegisterWorkspaceAsync(env.AdminOptions, otherWorkspace, "Other", Admin.Subject);
        await Failure(ListFailure.NotFound, () => env.Engine.GetListAsync(Admin, otherWorkspace, list.Id));
        Assert.Empty(await env.Engine.GetListsAsync(Admin, otherWorkspace));
        Assert.Equal(0, await UnscopedCountAsync(env.Runtime));
    }

    [Fact]
    public async Task ReceiptConflictsAndExpiryNeverReleasePermanentIdentity()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var request = Request(list);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        var baseline = await env.SnapshotAsync(list.Id);
        await Failure(ListFailure.Conflict, () => env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id,
            request with { Values = new JsonObject { [list.Fields[0].Id.ToString("D")] = "changed" } }));
        await Failure(ListFailure.Conflict, () => env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id,
            request with { SchemaVersion = 2 }));
        Assert.Equal(baseline, await env.SnapshotAsync(list.Id));
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, env.Workspace))
        {
            var receipt = await scope.Context.CreateReceipts.SingleAsync();
            receipt.ExpiresUtc = receipt.CreatedUtc.AddTicks(-1);
            await scope.Context.SaveChangesAsync();
            await scope.CommitAsync();
        }
        await Failure(ListFailure.Conflict, () => env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request));
        Assert.Equal(baseline, await env.SnapshotAsync(list.Id));
        // Simulate later receipt maintenance and record deletion. The lifetime claim survives both.
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, env.Workspace))
        {
            scope.Context.CreateReceipts.RemoveRange(await scope.Context.CreateReceipts.ToListAsync());
            scope.Context.Records.RemoveRange(await scope.Context.Records.ToListAsync());
            await scope.Context.SaveChangesAsync();
            await scope.CommitAsync();
        }
        await Failure(ListFailure.Conflict, () => env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request));
        var limited = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxRecordsPerList = 1 });
        await Failure(ListFailure.Limit, () => limited.CreateRecordAsync(Admin, env.Workspace, list.Id,
            request with { Id = Guid.NewGuid() }));
        Assert.Equal(1, (await env.SnapshotAsync(list.Id)).Identities);
    }

    [Fact]
    public async Task QuotasRollBackEveryPublicationAndConcurrentCreatesReserveOnce()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var baseline = await env.SnapshotAsync(list.Id);
        var request = Request(list);
        foreach (var limits in new[]
        {
            new ListLimits { MaxWorkspaceBytes = baseline.Bytes + 1 },
            new ListLimits { MaxAuditEvents = baseline.Audits }
        })
        {
            var limited = new SqlServerListEngine(env.RuntimeOptions, limits);
            await Failure(ListFailure.Limit, () => limited.CreateRecordAsync(Admin, env.Workspace, list.Id, request));
            Assert.Equal(baseline, await env.SnapshotAsync(list.Id));
        }
        var countLimited = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxRecordsPerList = 1 });
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            countLimited.CreateRecordAsync(Admin, env.Workspace, list.Id, request)));
        Assert.Single(results.Where(result => !result.Replayed));
        Assert.All(results, result => Assert.Equal(1, result.Number));
        var after = await env.SnapshotAsync(list.Id);
        Assert.Equal(1, after.Records);
        Assert.Equal(1, after.Claims);
        Assert.Equal(1, after.Receipts);
        Assert.Equal(1, after.Identities);
        Assert.Equal(2, after.NextNumber);
        Assert.Equal(baseline.Audits + 1, after.Audits);
        await Failure(ListFailure.Limit, () => countLimited.CreateRecordAsync(Admin, env.Workspace, list.Id,
            request with { Id = Guid.NewGuid() }));
        Assert.Equal(after, await env.SnapshotAsync(list.Id));
        // A failed quota attempt has left no identity claim. The original request remains usable.
        var failedRequest = request with { Id = Guid.NewGuid() };
        var unavailableQuota = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxWorkspaceBytes = after.Bytes + 1 });
        await Failure(ListFailure.Limit, () => unavailableQuota.CreateRecordAsync(Admin, env.Workspace, list.Id, failedRequest));
        var next = await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, failedRequest);
        Assert.Equal(2, next.Number);
        var concurrentLimit = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxRecordsPerList = 4 });
        var distinct = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            try { return await concurrentLimit.CreateRecordAsync(Admin, env.Workspace, list.Id, request with { Id = Guid.NewGuid() }); }
            catch (ListException ex) when (ex.Failure == ListFailure.Limit) { return null; }
        }));
        Assert.Equal(new long[] { 3, 4 }, distinct.OfType<CreateRecordResult>().Select(result => result.Number).Order().ToArray());
        var complete = await env.SnapshotAsync(list.Id);
        Assert.Equal(4, complete.Identities);
        Assert.Equal(4, complete.Claims);
        Assert.Equal(4, complete.Records);
        Assert.Equal(4, complete.Receipts);
        Assert.Equal(5, complete.NextNumber);
    }

    [Fact]
    public async Task PersistedInterceptorValuesDetermineReadValidationAndUtf8Charge()
    {
        var replacement = new ReplaceRecordJson();
        var env = await SetupAsync(replacement);
        var list = await env.CreateListAsync();
        var request = Request(list);
        replacement.Json = new JsonObject { [list.Fields[0].Id.ToString("D")] = "\U0001F680 different \u00e9" }.ToJsonString();
        var baseline = await env.SnapshotAsync(list.Id);
        var result = await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        var record = await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, request.Id);
        Assert.Equal(replacement.Json, record.Values.ToJsonString());
        var after = await env.SnapshotAsync(list.Id);
        var expected = Encoding.UTF8.GetByteCount(replacement.Json) + 192L +
            Encoding.UTF8.GetByteCount(ListValidation.CreateDigest(request)) + Encoding.UTF8.GetByteCount(Admin.Subject) + 128 +
            96 + Encoding.UTF8.GetByteCount(Admin.Subject) + Encoding.UTF8.GetByteCount("record.create") + 16;
        Assert.Equal(expected, after.Bytes - baseline.Bytes);
        Assert.Equal(result with { Replayed = true }, await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request));
        replacement.Json = new JsonObject { [list.Fields[0].Id.ToString("D")] = 42 }.ToJsonString();
        await Failure(ListFailure.Validation, () => env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id,
            request with { Id = Guid.NewGuid() }));
        Assert.Equal(after, await env.SnapshotAsync(list.Id));
        replacement.Json = new JsonObject { [list.Fields[0].Id.ToString("D")] = new string('x', 200) }.ToJsonString();
        var tiny = new SqlServerListEngine(env.OptionsWith(replacement), new ListLimits { MaxRecordBytes = 100 });
        await Failure(ListFailure.Limit, () => tiny.CreateRecordAsync(Admin, env.Workspace, list.Id,
            request with { Id = Guid.NewGuid() }));
        Assert.Equal(after, await env.SnapshotAsync(list.Id));
        Assert.Equal(0, await UnscopedCountAsync(env.Runtime));
    }

    [Fact]
    public async Task FailureCancellationAndFailedResetReleaseWorkspaceAuthority()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var baseline = await env.SnapshotAsync(list.Id);
        var request = Request(list);
        using var cancellation = new CancellationTokenSource();
        var cancel = new CancelAfterRecordSave(cancellation);
        var canceled = new SqlServerListEngine(env.OptionsWith(cancel));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.CreateRecordAsync(Admin, env.Workspace,
            list.Id, request, cancellation.Token));
        Assert.True(cancel.Triggered);
        Assert.Equal(baseline, await env.SnapshotAsync(list.Id));
        Assert.Equal(0, await UnscopedCountAsync(env.Runtime));
        var failure = new FailAfterRecordSave();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlServerListEngine(env.OptionsWith(failure))
            .CreateRecordAsync(Admin, env.Workspace, list.Id, request));
        Assert.Equal(baseline, await env.SnapshotAsync(list.Id));
        Assert.Equal(0, await UnscopedCountAsync(env.Runtime));

        await using var external = new SqlConnection(env.Runtime);
        await external.OpenAsync();
        var reset = new FailReset();
        var externalOptions = new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(external).AddInterceptors(reset).Options;
        var result = await new SqlServerListEngine(externalOptions).CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        Assert.Equal(1, result.Number);
        Assert.True(reset.Triggered);
        Assert.Equal(ConnectionState.Closed, external.State);
        // A known commit stays successful even when context reset fails.
        Assert.Equal(1, (await env.SnapshotAsync(list.Id)).Records);
        Assert.Equal(0, await UnscopedCountAsync(env.Runtime));
    }

    [Fact]
    public async Task PostLockClockAndConfiguredTimeoutGovernWaits()
    {
        var observed = new ObserveTimeouts();
        var env = await SetupAsync(observed);
        var list = await env.CreateListAsync();
        var request = Request(list);
        var shortOptions = new DbContextOptionsBuilder<SqlServerListDbContext>(env.RuntimeOptions)
            .UseSqlServer(sql => sql.CommandTimeout(1)).AddInterceptors(observed).Options;
        var limited = new SqlServerListEngine(shortOptions, new ListLimits { StatementTimeout = TimeSpan.FromMilliseconds(100) });
        await using (var blocker = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, env.Workspace))
        {
            await SqlServerListDatabase.LockAsync(blocker.Context, SqlServerListDatabase.WorkspaceLock(env.Workspace), true,
                new ListLimits(), default);
            await Failure(ListFailure.Unavailable, () => limited.CreateRecordAsync(Admin, env.Workspace, list.Id, request));
        }
        Assert.Contains(observed.CommandTimeouts, timeout => timeout == 1);
        Assert.Equal(0, (await env.SnapshotAsync(list.Id)).Records);
        Task<CreateRecordResult> pending;
        DateTimeOffset release;
        await using (var blocker = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, env.Workspace))
        {
            await SqlServerListDatabase.LockAsync(blocker.Context, SqlServerListDatabase.WorkspaceLock(env.Workspace), true,
                new ListLimits(), default);
            var attempted = new ObserveWorkspaceLock();
            pending = new SqlServerListEngine(env.OptionsWith(attempted)).CreateRecordAsync(Admin, env.Workspace, list.Id, request);
            await attempted.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release = await SqlServerListDatabase.ClockAsync(blocker.Context, default);
            await blocker.CommitAsync();
        }
        await pending;
        var record = await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, request.Id);
        Assert.True(record.CreatedUtc >= release);
    }

    [Fact]
    public async Task UnknownCommitOutcomeIsResolvedByOriginalCreateRetry()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var request = Request(list);
        var uncertain = new LoseCommitAcknowledgment();
        await Failure(ListFailure.Unavailable, () => new SqlServerListEngine(env.OptionsWith(uncertain))
            .CreateRecordAsync(Admin, env.Workspace, list.Id, request));
        Assert.True(uncertain.Triggered);
        var committed = await env.SnapshotAsync(list.Id);
        Assert.Equal(1, committed.Records);
        var replay = await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        Assert.True(replay.Replayed);
        Assert.Equal(1, replay.Number);
        Assert.Equal(committed, await env.SnapshotAsync(list.Id));
        Assert.Equal(0, await UnscopedCountAsync(env.Runtime));
    }

    [Fact]
    public async Task ExactSubjectsAndUnsupportedFeaturesNeverWrite()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        foreach (var subject in new[] { "actor", "actor ", "ACTOR", "\U0001F680", new string('x', 1024) })
        {
            await env.SetMemberAsync(subject);
            await env.SetGrantAsync(list.Id, subject, subject == "actor " ? ListAccess.Read : ListAccess.None);
        }
        Assert.Equal(list.Id, (await env.Engine.GetListAsync(new("actor "), env.Workspace, list.Id)).Id);
        await Failure(ListFailure.NotFound, () => env.Engine.GetListAsync(new("actor"), env.Workspace, list.Id));
        await Failure(ListFailure.NotFound, () => env.Engine.GetListAsync(new("ACTOR"), env.Workspace, list.Id));
        var before = await env.SnapshotAsync(list.Id);
        var id = Guid.NewGuid();
        var calls = new Func<Task>[]
        {
            () => env.Engine.PublishSchemaAsync(Admin, env.Workspace, list.Id, new(1, "Rows", list.Fields)),
            () => env.Engine.UpdateRecordAsync(Admin, env.Workspace, list.Id, id, new(1, 1, [])),
            () => env.Engine.DeleteRecordAsync(Admin, env.Workspace, list.Id, id, 1),
            () => env.Engine.QueryAsync(Admin, env.Workspace, list.Id, new()),
            () => env.Engine.PreviewMergeAsync(Admin, env.Workspace, list.Id, id, new(1, new JsonObject(), [])),
            () => env.Engine.GetViewsAsync(Admin, env.Workspace, list.Id),
            () => env.Engine.SaveViewAsync(Admin, env.Workspace, list.Id, new(id, "View", new(), [])),
            () => env.Engine.ResolveReferenceAsync(Admin, env.Workspace, list.Id, id, id),
            () => env.Engine.SetMembershipAsync(Admin, env.Workspace, "new", true, true, true),
            () => env.Engine.SetListGrantAsync(Admin, env.Workspace, list.Id, "actor", ListAccess.Write),
            () => env.Engine.GetAuditAsync(Admin, env.Workspace),
            () => env.Engine.CreateListAsync(Admin, env.Workspace, NewListRequest() with { TemplateId = id, TemplateVersion = 1 }),
            () => env.Engine.CreateListAsync(Admin, env.Workspace, NewListRequest() with
                { Fields = [new(id, "Ref", ListFieldType.Reference, ReferenceListId: list.Id)] })
        };
        foreach (var call in calls) await Failure(ListFailure.NotSupported, call);
        Assert.Equal(before, await env.SnapshotAsync(list.Id));
        var hostWorkspace = Guid.NewGuid();
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, hostWorkspace))
        {
            scope.Context.Workspaces.Add(new WorkspaceRow { WorkspaceId = hostWorkspace, Name = "Host",
                AuthorizationMode = "host", AuthorizationDomain = "policy", AuthorizationScope = "tenant" });
            await scope.Context.SaveChangesAsync();
            await scope.CommitAsync();
        }
        await Failure(ListFailure.NotSupported, () => env.Engine.CreateListAsync(Admin, hostWorkspace, NewListRequest()));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Engine.CreateRecordAsync(Admin, env.Workspace,
            list.Id, Request(list), canceled.Token));
        Assert.Equal(before, await env.SnapshotAsync(list.Id));
    }

    [Fact]
    public void RetryingStrategiesAndMarsAreRejectedBeforeConnectionUse()
    {
        var options = new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(
            "Server=unused;Database=unused;Integrated Security=true", sql => sql.EnableRetryOnFailure()).Options;
        Assert.Throws<ArgumentException>(() => new SqlServerListEngine(options));
        var mars = new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(
            "Server=unused;Database=unused;Integrated Security=true;MultipleActiveResultSets=true").Options;
        Assert.Throws<ArgumentException>(() => new SqlServerListEngine(mars));
    }

    private async Task<Environment> SetupAsync(params IInterceptor[] interceptors)
    {
        var admin = await fixture.CreateDatabaseAsync();
        var adminOptions = new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(admin).Options;
        await new SqlServerListMigrator(adminOptions).MigrateAsync();
        var workspace = Guid.NewGuid();
        await SqlServerListSetup.RegisterWorkspaceAsync(adminOptions, workspace, "Runtime tests", Admin.Subject);
        var user = "cb_lists_" + Guid.NewGuid().ToString("N");
        var password = "Cb!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using (var connection = new SqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var setup = new SqlCommand($"""
                CREATE LOGIN [{user}] WITH PASSWORD='{password}';
                CREATE USER [{user}] FOR LOGIN [{user}];
                GRANT VIEW DEFINITION ON SCHEMA::cellbridge_lists TO [{user}];
                """, connection);
            await setup.ExecuteNonQueryAsync();
            foreach (var (table, permissions) in SqlServerListEngine.RuntimePermissions)
            {
                await using var grant = new SqlCommand($"GRANT {string.Join(',', permissions)} ON cellbridge_lists.[{table}] TO [{user}]", connection);
                await grant.ExecuteNonQueryAsync();
            }
        }
        var runtime = new SqlConnectionStringBuilder(admin) { UserID = user, Password = password, MaxPoolSize = 8 }.ConnectionString;
        var runtimeOptions = new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(runtime).AddInterceptors(interceptors).Options;
        return new Environment(adminOptions, runtimeOptions, runtime, workspace);
    }

    private static CreateListRequest NewListRequest() => new(Guid.NewGuid(), "Rows",
        [new ListField(Guid.NewGuid(), "Text", ListFieldType.Text)]);
    private static CreateRecordRequest Request(ListDefinition list) => new(Guid.NewGuid(), 1,
        new JsonObject { [list.Fields[0].Id.ToString("D")] = "initial" });
    private static async Task Failure(ListFailure expected, Func<Task> operation) =>
        Assert.Equal(expected, (await Assert.ThrowsAsync<ListException>(operation)).Failure);
    private static async Task<int> UnscopedCountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM cellbridge_lists.records", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Snapshot(long Bytes, long Audits, long Identities, long NextNumber, int Records, int Claims, int Receipts);
    private sealed class Environment(DbContextOptions<SqlServerListDbContext> adminOptions,
        DbContextOptions<SqlServerListDbContext> runtimeOptions, string runtime, Guid workspace)
    {
        internal DbContextOptions<SqlServerListDbContext> AdminOptions => adminOptions;
        internal DbContextOptions<SqlServerListDbContext> RuntimeOptions => runtimeOptions;
        internal string Runtime => runtime;
        internal Guid Workspace => workspace;
        internal SqlServerListEngine Engine { get; } = new(runtimeOptions);
        internal DbContextOptions<SqlServerListDbContext> OptionsWith(params IInterceptor[] interceptors) =>
            new DbContextOptionsBuilder<SqlServerListDbContext>(runtimeOptions).AddInterceptors(interceptors).Options;
        internal Task<ListDefinition> CreateListAsync() => Engine.CreateListAsync(Admin, workspace, NewListRequest());
        internal async Task<Snapshot> SnapshotAsync(Guid listId)
        {
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(adminOptions, workspace);
            var db = scope.Context;
            var row = await db.Workspaces.SingleAsync();
            var counter = await db.ListCounters.SingleAsync(value => value.ListId == listId);
            return new(row.StoredBytes, row.AuditCount, counter.IdentityCount, counter.NextNumber,
                await db.Records.CountAsync(value => value.ListId == listId),
                await db.RecordTombstones.CountAsync(value => value.ListId == listId),
                await db.CreateReceipts.CountAsync(value => value.ListId == listId));
        }
        internal async Task SetMemberAsync(string subject, bool create = false, bool manage = false, bool remove = false)
        {
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(adminOptions, workspace);
            var db = scope.Context;
            await SqlServerListDatabase.LockAsync(db, SqlServerListDatabase.WorkspaceLock(workspace), true, new ListLimits(), default);
            var key = SqlServerListSetup.SubjectKey(subject);
            var row = await db.Memberships.SingleOrDefaultAsync(value => EF.Property<byte[]>(value, "SubjectKey") == key);
            if (remove)
            {
                // The FK requires grant removal when removing membership.
                db.ListGrants.RemoveRange(await db.ListGrants.Where(value => EF.Property<byte[]>(value, "SubjectKey") == key).ToListAsync());
                if (row is not null) db.Memberships.Remove(row);
            }
            else
            {
                if (row is null) { row = new MembershipRow { WorkspaceId = workspace, Subject = subject }; db.Memberships.Add(row); }
                row.CanCreateLists = create;
                row.CanManageGrants = manage;
            }
            await db.SaveChangesAsync();
            await scope.CommitAsync();
        }
        internal async Task SetGrantAsync(Guid listId, string subject, ListAccess access)
        {
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(adminOptions, workspace);
            var db = scope.Context;
            await SqlServerListDatabase.LockAsync(db, SqlServerListDatabase.WorkspaceLock(workspace), true, new ListLimits(), default);
            var key = SqlServerListSetup.SubjectKey(subject);
            var row = await db.ListGrants.SingleOrDefaultAsync(value => value.ListId == listId && EF.Property<byte[]>(value, "SubjectKey") == key);
            if (row is null) { row = new ListGrantRow { WorkspaceId = workspace, ListId = listId, Subject = subject }; db.ListGrants.Add(row); }
            row.Access = (int)access;
            await db.SaveChangesAsync();
            await scope.CommitAsync();
        }
    }

    private sealed class ObserveReceiptReads : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [cellbridge_lists].[create_receipts]", StringComparison.Ordinal)) Count++;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ReplaceRecordJson : SaveChangesInterceptor
    {
        public string? Json;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Json is not null)
                foreach (var entry in eventData.Context!.ChangeTracker.Entries<RecordRow>().Where(entry => entry.State == EntityState.Added))
                    entry.Entity.ValuesJson = Json;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CancelAfterRecordSave(CancellationTokenSource source) : SaveChangesInterceptor
    {
        public bool Triggered;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<RecordRow>().Any()) { Triggered = true; source.Cancel(); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FailAfterRecordSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<RecordRow>().Any()) throw new InvalidOperationException("Injected post-save failure.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FailReset : DbCommandInterceptor
    {
        public bool Triggered;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SET CONTEXT_INFO 0x;", StringComparison.Ordinal))
            { Triggered = true; throw new InvalidOperationException("Injected reset failure."); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ObserveTimeouts : DbCommandInterceptor
    {
        public List<int> CommandTimeouts { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            CommandTimeouts.Add(command.CommandTimeout);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ObserveWorkspaceLock : DbCommandInterceptor
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("sys.sp_getapplock", StringComparison.Ordinal)) Attempted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class LoseCommitAcknowledgment : DbTransactionInterceptor
    {
        public bool Triggered;
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (!eventData.Context!.ChangeTracker.Entries<RecordRow>().Any()) return;
            Triggered = true;
            // The server has committed. Raise a real SqlException after that commit
            // to simulate loss of the successful acknowledgment at the caller.
            await using var command = eventData.Context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "THROW 51008, 'Injected lost commit acknowledgment.', 1;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
