using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.OfficeInspectors;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using CellBridge.Tests;
using Npgsql;

namespace CellBridge.Storage.Tests;

[CollectionDefinition("Disposable database recovery", DisableParallelization = true)]
public sealed class DatabaseRecoveryCollection : ICollectionFixture<DatabaseRecoveryFixture>;

[Collection("Disposable database recovery")]
public sealed class DatabaseRecoveryTests
{
    [DatabaseRecoveryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DatabaseTerminationBeforePublicationRollsBackAndRetryPublishesOnce(bool filesystem) =>
        WithRecoveryDatabase(async db =>
        {
            var control = new DisposableDatabase();
            var root = filesystem ? control.Directory("interruption-content") : null;
            var provider = Provider(db, root);
            var service = new CellBridgeDocumentService(provider);
            var initial = (await service.CreateAsync("/interrupted.docx", MinimalDocx.Create(), TestActor.Value))!;
            Assert.True(await new ExternalRevisionPublisher(provider, new UnusedDestination()).BindAsync(initial.ResourceId,
                initial.LifecycleGeneration, initial.StateVersion, Guid.NewGuid(), "outage-destination", "initial"));
            var before = await Capture(provider);
            var request = StorageTests.Fixture("save-first");
            await SupplyExpectedIndex(service, initial, request);
            var gate = new NormalizedHeadPause(initial.ResourceId);
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(db).AddInterceptors(gate).Options;
            IContentStore interruptedContent = root is null ? new PostgreSqlContentStore(options) : new PostgreSqlFileSystemContentStore(options, root);
            var interrupted = new CellBridgeDocumentService(new(new PostgreSqlStateStore(options, validationContent: interruptedContent), interruptedContent));
            // The test interceptor pauses after actual normalized-head UPDATE SQL and before COMMIT.
            var save = Task.Run(async () => await interrupted.ExecuteAsync(initial.ResourceId,
                DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value));
            var reserved = before;
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                reserved = await Capture(provider);
                var unreserved = reserved.Snapshot.Snapshots.Select(snapshot => snapshot with
                {
                    ContentReferences = snapshot.ContentReferences.Where(reference => !reference.IsPending).ToImmutableArray(),
                    Current = snapshot.Current with { Head = snapshot.Current.Head with { Counters = snapshot.Current.Head.Counters with
                        { StoredBytes = snapshot.Current.Head.Counters.StoredBytes - snapshot.ContentReferences.Where(reference => reference.IsPending)
                            .Sum(reference => reference.Handle.Length + reference.AdmissionBytes) } } },
                });
                Assert.Contains(reserved.Snapshot.Snapshots.SelectMany(snapshot => snapshot.ContentReferences), reference => reference.IsPending);
                Assert.Equal(PortabilityTests.CanonicalSnapshots(before.Snapshot.Snapshots), PortabilityTests.CanonicalSnapshots(unreserved));
                await control.Kill();
                gate.Continue.TrySetResult();
                await Assert.ThrowsAsync<StorageUnavailableException>(() => save.WaitAsync(TimeSpan.FromSeconds(30)));
                await Assert.ThrowsAsync<StorageUnavailableException>(() => provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)).AsTask());
                await Assert.ThrowsAsync<StorageUnavailableException>(() => service.CreateAsync("/outage-must-not-exist.docx",
                    MinimalDocx.Create(), TestActor.Value).AsTask());
            }
            finally
            {
                gate.Continue.TrySetResult();
                // Restart before WithDatabase's finally needs the admin connection for DROP DATABASE.
                await control.Start();
            }
            // Check both the existing pool and a fresh pool BEFORE retry, without initialization or pool clearing.
            await Verify(provider, reserved);
            await using var fresh = Reopen(db);
            await Verify(Provider(fresh, root), reserved);
            Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Path("/OUTAGE-MUST-NOT-EXIST.DOCX")));
            await VerifyInventory(db);
            var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
            Assert.Equal(initial.ContentVersion + 1, saved.State.ContentVersion);
            Assert.Equal(1, saved.State.Counters.SaveReceipts);
            Assert.Equal(1, saved.State.Counters.PendingDeliveries);
            Assert.Equal(initial.Counters.Revisions + 1, saved.State.Counters.Revisions);
            VerifyIndependentReply(saved.Response.ToByteArray());
            var published = await Capture(provider);
            var retry = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(retry.Response.SubResponses).Status);
            await Verify(provider, published);
            await VerifyInventory(db);
            await control.Report("interruption", filesystem, new { boundary = "normalized-head UPDATE executed inside transaction, before COMMIT", snapshots = before.Snapshot.Snapshots.Length });
        });

    [DatabaseRecoveryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CleanDatabaseRestartPreservesAcknowledgedHistoryAndContent(bool filesystem) =>
        WithRecoveryDatabase(async db =>
        {
            var control = new DisposableDatabase();
            var root = filesystem ? control.Directory("restart-content") : null;
            var provider = Provider(db, root);
            var original = await PortabilityTests.History(provider, Guid.NewGuid());
            var before = await Capture(provider);
            var rows = await RawRows(db);
            try
            {
                await control.Stop();
                await Assert.ThrowsAsync<StorageUnavailableException>(() => provider.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)).AsTask());
            }
            finally { await control.Start(); }
            Assert.Equal(JsonSerializer.Serialize(rows), JsonSerializer.Serialize(await RawRows(db)));
            await Verify(provider, before);
            await using var fresh = Reopen(db);
            await Verify(Provider(fresh, root), before);
            await VerifyInventory(db);
            await control.Report("restart", filesystem, new { boundary = "acknowledged saves followed by clean database stop/start", snapshots = before.Snapshot.Snapshots.Length });
        });

    [DatabaseRecoveryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LogicalBackupRestoresRawStateEveryContentRootAndCanContinue(bool filesystem) =>
        WithRecoveryDatabase(async db =>
        {
            var control = new DisposableDatabase();
            var root = filesystem ? control.Directory("backup-source-content") : null;
            var provider = Provider(db, root);
            var original = await PortabilityTests.History(provider, Guid.NewGuid());
            Assert.True(original.Counters.SaveReceipts > 0);
            Assert.True(original.Counters.RestoreReceipts > 0);
            Assert.True(original.Counters.PendingDeliveries > 0);
            Assert.Contains((await provider.Documents.ReadControlAsync(original.ResourceId))!.Partitions, p => p.Kind == (int)DocumentPartitionKind.Metadata && p.ElementCount > 0);
            var service = new CellBridgeDocumentService(provider);
            var retired = (await service.CreateAsync("/retired.docx", MinimalDocx.Create("retired"), TestActor.Value))!;
            Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(retired.ResourceId,
                retired.LifecycleGeneration, retired.StateVersion))).Applied);
            // No writers, external publishers or collectors run against these private databases/roots.
            var before = await Capture(provider);
            foreach (var reply in before.Decoders)
                await System.IO.File.WriteAllBytesAsync(control.File("source-reply-" + reply.Key + ".bin"), before.Bytes[reply.Key]);
            await System.IO.File.WriteAllTextAsync(control.File("source-replies.json"), JsonSerializer.Serialize(new
            {
                sourceFixture = "PortabilityTests.History: MetadataPublicationTests.Initial(GraphFixture), synthetic metadata and DOCX inline file graphs; not a desktop capture",
                decoders = before.Decoders
            }, new JsonSerializerOptions { WriteIndented = true }));
            var activeHandles = before.Snapshot.Snapshots.SelectMany(document => document.Visibility.Where(visibility => visibility.EndSequence is null)
                .Select(visibility => document.Elements.Single(element => element.Scope == visibility.Scope && element.Element.Id == visibility.Element).Element.Payload.Key)).ToHashSet();
            var historyOnly = before.Handles.First(handle => !activeHandles.Contains(handle.Key));
            var rows = await RawRows(db);
            var archive = control.File("database.dump");
            await control.Dump(db, archive);
            var copiedRoot = filesystem ? control.Directory("backup-restored-content") : null;
            if (filesystem) CopyObjects(root!, copiedRoot!);
            var restoredName = "cellbridge_restore_" + Guid.NewGuid().ToString("N");
            await using var admin = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
            await using (var create = admin.CreateCommand($"CREATE DATABASE {restoredName}")) await create.ExecuteNonQueryAsync();
            try
            {
                await control.Restore(restoredName, archive);
                var connection = Connection(db); connection.Database = restoredName;
                await using var restoredDb = NpgsqlDataSource.Create(connection.ConnectionString);
                // Open the restored schema directly, without running initialization.
                Assert.Equal(JsonSerializer.Serialize(rows), JsonSerializer.Serialize(await RawRows(restoredDb)));
                var restored = Provider(restoredDb, copiedRoot);
                await Verify(restored, before);
                await VerifyInventory(restoredDb);
                Assert.Null(await restored.Documents.FindAsync(new DocumentIdentity.Resource(retired.ResourceId)));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CellBridgeDocumentService(restored)
                    .GetRevisionAsync(original.ResourceId, 1, new(new("foreign", "foreign", "foreign"))).AsTask());
                if (filesystem)
                {
                    // The full oracle must reject content used only by an old provider snapshot,
                    // even when current downloads would succeed. Each defect has its own copy.
                    foreach (var defect in new[] { "missing", "corrupt" })
                    {
                        var badRoot = control.Directory(defect + "-content");
                        CopyObjects(copiedRoot!, badRoot);
                        var path = Path.Combine(badRoot, historyOnly.Key + ".blob");
                        if (defect == "missing") File.Delete(path);
                        else
                        {
                            var bytes = await File.ReadAllBytesAsync(path); bytes[0] ^= 1;
                            await File.WriteAllBytesAsync(path, bytes);
                        }
                        await Assert.ThrowsAsync<StorageCorruptionException>(() => Verify(Provider(restoredDb, badRoot), before));
                    }
                }
                var restoredService = new CellBridgeDocumentService(restored);
                var revision = await restoredService.RestoreRevisionAsync(original.ResourceId, 3,
                    original.LatestRevisionNumber, "after-logical-backup", TestActor.Value);
                Assert.Equal(before.Snapshot.Snapshots.Single(document => document.Current.Head.ResourceId == original.ResourceId).ApplicationRevisions.Single(record => record.RevisionNumber == 3).Package.Sha256, revision.Revision.Package.Sha256);
                var published = await Capture(restored);
                var retry = await restoredService.RestoreRevisionAsync(original.ResourceId, 3,
                    original.LatestRevisionNumber, "after-logical-backup", TestActor.Value);
                Assert.Equal(JsonSerializer.Serialize(revision.Revision), JsonSerializer.Serialize(retry.Revision));
                await Verify(restored, published);
                // A new reference PutChanges on another document must also survive receipt retry.
                await CheckReferenceSave(restored);
                await VerifyInventory(restoredDb);
                await using var archiveStream = System.IO.File.OpenRead(archive);
                await control.Report("backup", filesystem, new { snapshots = before.Snapshot.Snapshots.Length,
                    contentObjects = before.Handles.Length, historyOnly = historyOnly.Key,
                    sourceFixture = "PortabilityTests.History: MetadataPublicationTests.Initial(GraphFixture), synthetic metadata and DOCX inline file graphs; not a desktop capture",
                    independentReceiptDecoding = before.Decoders,
                    positiveControl = "SharePoint save_reopen/save-first and save-second requests; generated post-restore file save replies fully consumed independently",
                    archiveSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(archiveStream)) });
            }
            finally
            {
                await using var drop = admin.CreateCommand($"DROP DATABASE {restoredName} WITH (FORCE)");
                await drop.ExecuteNonQueryAsync();
            }
        });

    private static async Task WithRecoveryDatabase(Func<NpgsqlDataSource, Task> test)
    {
        await new DisposableDatabase().ValidateEndpoint();
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!)
            { Timeout = 5, CommandTimeout = 10 };
        var database = "cellbridge_recovery_" + Guid.NewGuid().ToString("N");
        await using (var admin = NpgsqlDataSource.Create(connection.ConnectionString))
        await using (var create = admin.CreateCommand($"CREATE DATABASE {database}")) await create.ExecuteNonQueryAsync();
        Exception? failure = null;
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Database = database };
            await using var db = NpgsqlDataSource.Create(scoped.ConnectionString);
            await new PostgreSqlStateStore(db).InitializeAsync();
            await test(db);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            // The pre-fault admin pool also has dead idle connections. Cleanup
            // uses a fresh pool and preserves a test failure if DROP also fails.
            try
            {
                await using var admin = NpgsqlDataSource.Create(connection.ConnectionString);
                await using var drop = admin.CreateCommand($"DROP DATABASE {database} WITH (FORCE)");
                await drop.ExecuteNonQueryAsync();
            }
            catch (Exception cleanup) when (failure is not null) { throw new AggregateException(failure, cleanup); }
        }
    }

    private static StorageProvider Provider(NpgsqlDataSource db, string? root)
    {
        IContentStore content = root is null ? new PostgreSqlContentStore(db) : new PostgreSqlFileSystemContentStore(db, root);
        return new(new PostgreSqlStateStore(db, validationContent: content), content);
    }

    private static NpgsqlConnectionStringBuilder Connection(NpgsqlDataSource db) => new(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!)
        { Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database, Timeout = 5, CommandTimeout = 10 };
    private static NpgsqlDataSource Reopen(NpgsqlDataSource db) => NpgsqlDataSource.Create(Connection(db).ConnectionString);

    private sealed record DecoderEvidence(bool Parsed, int Status, long Consumed, int ByteLength, int ProtocolVersion,
        int MinimumVersion, string? Error);
    private static DecoderEvidence Decode(byte[] bytes)
    {
        var result = OfficeInspector.ParseResponse(bytes);
        return new(result.Parsed, result.Response.Status, result.Consumed, bytes.Length, result.Response.ProtocolVersion,
            result.Response.MinimumVersion, result.Error?.Split('\n')[0].Trim());
    }
    private sealed record Expected(ProviderSnapshot Snapshot, ContentHandle[] Handles, Dictionary<string, byte[]> Bytes,
        Dictionary<string, DecoderEvidence> Decoders);
    private static async Task<Expected> Capture(StorageProvider provider)
    {
        var snapshot = await ((IProviderRecoveryStore)provider.Documents).CaptureRecoveryAsync();
        var handles = ContentStoreReader.UniqueHandles(snapshot.Snapshots.SelectMany(document => document.ContentReferences.Where(reference => !reference.IsPending).Select(reference => reference.Handle))).ToArray();
        var bytes = new Dictionary<string, byte[]>();
        foreach (var handle in handles) bytes.Add(handle.Key, await provider.Content.ReadVerifiedAsync(handle));
        var decoders = snapshot.Snapshots.SelectMany(s => s.Receipts).Where(r => r.Response is not null)
            .Select(r => r.Response!).DistinctBy(h => h.Key).ToDictionary(h => h.Key, h => Decode(bytes[h.Key]));
        return new(snapshot, handles, bytes, decoders);
    }

    private static async Task Verify(StorageProvider provider, Expected expected)
    {
        var actual = await provider.Documents.CaptureRecoveryAsync();
        Assert.Equal(PortabilityTests.CanonicalSnapshots(expected.Snapshot.Snapshots), PortabilityTests.CanonicalSnapshots(actual.Snapshots));
        Assert.Equal(JsonSerializer.Serialize(expected.Snapshot.Receipts), JsonSerializer.Serialize(actual.Receipts));
        foreach (var handle in expected.Handles)
        {
            await using var stream = await provider.Content.OpenReadAsync(handle);
            using var output = new MemoryStream(); await stream.CopyToAsync(output);
            var bytes = output.ToArray();
            Assert.Equal(handle.Length, bytes.LongLength);
            Assert.Equal(handle.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.Equal(expected.Bytes[handle.Key], bytes);
            if (expected.Decoders.TryGetValue(handle.Key, out var decoder))
            {
                var restoredDecoder = Decode(bytes);
                Assert.Equal(decoder, restoredDecoder);
                Assert.True(restoredDecoder.Parsed, restoredDecoder.Error);
                Assert.Equal(0, restoredDecoder.Status);
            }
        }
        foreach (var snapshot in expected.Snapshot.Snapshots)
        {
            await VerifyGraph(snapshot.Current.Head.Package, provider);
            foreach (var revision in snapshot.ApplicationRevisions) await VerifyGraph(revision.Package, provider);
        }
    }

    private static async Task VerifyGraph(DocumentPackage package, StorageProvider provider)
    {
        var bytes = await StorageTestDocuments.PackageAsync(provider, package);
        Assert.Equal(package.Length, bytes.LongLength);
        Assert.Equal(package.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static void VerifyIndependentReply(byte[] bytes)
    {
        var inspection = OfficeInspector.ParseResponse(bytes);
        Assert.True(inspection.Parsed, inspection.Error);
        Assert.Equal(0, inspection.Response.Status);
    }

    private static async Task CheckReferenceSave(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/reference-save.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = StorageTests.Fixture("save-first");
        await SupplyExpectedIndex(service, initial, request);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        VerifyIndependentReply(saved.Response.ToByteArray());
        var expected = await Capture(provider);
        var retry = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(retry.Response.SubResponses).Status);
        Assert.Equal(saved.Response.ToByteArray(), retry.Response.ToByteArray());
        await Verify(provider, expected);
        var second = StorageTests.Fixture("save-second");
        await SupplyExpectedIndex(service, saved.State, second);
        var continued = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            second, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(continued.Response.SubResponses).Status);
        VerifyIndependentReply(continued.Response.ToByteArray());
        await VerifyGraph(continued.State.Package, provider);
    }

    private static async Task<SortedDictionary<string, string[]>> RawRows(NpgsqlDataSource db)
    {
        var result = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        await using var tables = db.CreateCommand("SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename");
        var names = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            if (name != "__CellBridgeDocumentMigrationsHistory")
                Assert.Matches("^cellbridge_[a-z_]+$", name);
            await using var query = db.CreateCommand($"SELECT row FROM (SELECT to_jsonb(t)::text AS row FROM \"{name}\" t) rows ORDER BY row COLLATE \"C\"");
            var rows = new List<string>();
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            result.Add(name, rows.ToArray());
        }
        return result;
    }

    private static async Task VerifyInventory(NpgsqlDataSource db)
    {
        await new StorageMaintenance(db).CollectOrphansAsync();
    }

    private static void CopyObjects(string source, string target)
    {
        foreach (var path in System.IO.Directory.EnumerateFiles(source))
            System.IO.File.Copy(path, Path.Combine(target, Path.GetFileName(path)), overwrite: false);
    }

    private static async Task SupplyExpectedIndex(CellBridgeDocumentService service, DocumentHead head, FsshttpbCellRequest request)
    {
        var expected = (await StorageTestDocuments.GraphAsync(service, head)).StorageIndexes.Single();
        ((PutChangesSubRequestData)request.SubRequests.Single().Data!).ExpectedStorageIndex = expected.DataElementExtendedGuid;
        request.DataElementPackage!.DataElements.RemoveAll(element => element.DataElementExtendedGuid.Equals(expected.DataElementExtendedGuid));
        request.DataElementPackage.DataElements.Add(expected);
    }

    private sealed class UnusedDestination : IExternalRevisionDestination
    {
        public ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream content,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Only queue delivery during qualification.");
    }

    private sealed class DisposableDatabase
    {
        private readonly string id = Environment.GetEnvironmentVariable("CELLBRIDGE_RECOVERY_CONTAINER")!;
        private readonly string marker = Environment.GetEnvironmentVariable("CELLBRIDGE_RECOVERY_RUN")!;
        private readonly string output = Environment.GetEnvironmentVariable("CELLBRIDGE_RECOVERY_OUTPUT")!;
        private readonly string run = Guid.NewGuid().ToString("N");
        public string Directory(string name) { var path = File(name); System.IO.Directory.CreateDirectory(path); return path; }
        public string File(string name) => Path.Combine(output, run + "-" + name);

        private async Task<JsonElement> Inspect()
        {
            Assert.Matches("^[0-9a-f]{64}$", id); Assert.Matches("^[0-9a-f]{32}$", marker);
            using var json = JsonDocument.Parse(await Command("docker", "inspect", id));
            var container = json.RootElement[0];
            Assert.Equal(id, container.GetProperty("Id").GetString());
            var config = container.GetProperty("Config");
            Assert.Equal(marker, config.GetProperty("Labels").GetProperty("cellbridge.recovery").GetString());
            Assert.Contains(config.GetProperty("Image").GetString(), new[] { "postgres:18.3", "docker.io/library/postgres:18.3" });
            var env = config.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.True(env.Contains("CELLBRIDGE_RECOVERY_RUN=" + marker), "Container run marker does not match.");
            Assert.True(env.Contains("PGDATA=/cellbridge-recovery-data/" + marker), "Container PGDATA is not disposable.");
            var tmpfs = container.GetProperty("HostConfig").GetProperty("Tmpfs");
            Assert.Single(tmpfs.EnumerateObject());
            Assert.Equal("rw", tmpfs.GetProperty("/var/lib/postgresql").GetString());
            foreach (var mount in container.GetProperty("Mounts").EnumerateArray())
            { Assert.Equal("tmpfs", mount.GetProperty("Type").GetString()); Assert.Equal("/var/lib/postgresql", mount.GetProperty("Destination").GetString()); }
            return container.Clone();
        }

        public async Task ValidateEndpoint()
        {
            await Inspect();
            await using var db = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
            await using var query = db.CreateCommand("SELECT current_setting('data_directory')");
            Assert.Equal("/cellbridge-recovery-data/" + marker, await query.ExecuteScalarAsync());
        }

        public async Task Kill() { await ValidateEndpoint(); await Command("docker", "kill", "--signal", "KILL", id); await Stopped(137); }
        public async Task Stop() { await ValidateEndpoint(); await Command("docker", "stop", "--time", "30", id); await Stopped(0); }
        private async Task Stopped(int exit)
        {
            var state = (await Inspect()).GetProperty("State");
            Assert.False(state.GetProperty("Running").GetBoolean()); Assert.Equal(exit, state.GetProperty("ExitCode").GetInt32());
        }
        public async Task Start()
        {
            await Inspect();
            await Command("docker", "start", id);
            // Aspire resource start recreates the container after an external
            // kill. This fault test must retain the exact PGDATA instead. Initial
            // startup uses Hosting.Testing health checks; here SQL proves recovery of the same server.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                try { await ValidateEndpoint(); break; }
                catch (NpgsqlException) when (!timeout.IsCancellationRequested) { await Task.Delay(100, timeout.Token); }
            }
            Assert.True((await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean());
        }

        public async Task Dump(NpgsqlDataSource db, string path)
        {
            await ValidateEndpoint();
            var name = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database!;
            var local = "/tmp/" + run + ".dump";
            await Command("docker", "exec", "--env", "PGPASSWORD", id, "pg_dump", "--host", "127.0.0.1",
                "--username", "postgres", "--format=custom", "--file", local, name);
            await Command("docker", "cp", id + ":" + local, path);
        }
        public async Task Restore(string database, string path)
        {
            await ValidateEndpoint(); var local = "/tmp/" + run + "-restore.dump";
            await Command("docker", "cp", path, id + ":" + local);
            await Command("docker", "exec", "--env", "PGPASSWORD", id, "pg_restore", "--host", "127.0.0.1",
                "--username", "postgres", "--single-transaction",
                "--exit-on-error", "--dbname", database, local);
        }
        public async Task Report(string scenario, bool filesystem, object evidence)
        {
            await using var db = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
            await using var query = db.CreateCommand("SELECT version(), current_setting('fsync'), current_setting('synchronous_commit'), current_setting('full_page_writes')");
            await using var reader = await query.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal("on", reader.GetString(1)); Assert.Equal("on", reader.GetString(2)); Assert.Equal("on", reader.GetString(3));
            var report = new { scenario, content = filesystem ? "filesystem" : "postgresql", evidence,
                postgres = reader.GetString(0), fsync = reader.GetString(1), synchronousCommit = reader.GetString(2), fullPageWrites = reader.GetString(3),
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                npgsql = typeof(NpgsqlDataSource).Assembly.GetName().Version!.ToString(),
                pgDump = (await Command("docker", "exec", id, "pg_dump", "--version")).Trim(),
                pgRestore = (await Command("docker", "exec", id, "pg_restore", "--version")).Trim(),
                containerOs = await Command("docker", "exec", id, "cat", "/etc/os-release"),
                contentMount = await Command("findmnt", "-J", "-T", output),
                databaseMount = await Command("docker", "exec", id, "df", "-T", "/cellbridge-recovery-data/" + marker) };
            await System.IO.File.WriteAllTextAsync(File(scenario + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static async Task<string> Command(string executable, params string[] args)
        {
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
            // docker exec --env PGPASSWORD reads this CLI environment value. The
            // generated credential never appears in process arguments or reports.
            start.Environment["PGPASSWORD"] = new NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!).Password;
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120)); }
            catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
            // Raw inspect output includes credentials. Never include command output in assertion failures.
            var errors = await error;
            Assert.True(process.ExitCode == 0, $"{executable} {args[0]} failed with exit code {process.ExitCode}. {errors}");
            return await output;
        }
    }
}

public sealed class DatabaseRecoveryTheoryAttribute : TheoryAttribute
{
    public DatabaseRecoveryTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Controlled PostgreSQL interruption and filesystem restore checks require Linux.";
    }
}
