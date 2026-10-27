using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using CellBridge.TestInfrastructure;
using Npgsql;

namespace CellBridge.Benchmarks.Tests;

public sealed class DocumentLoadSeederTests
{
    [Fact]
    public async Task SeededDocumentsRetainGraphsAndSaveIndependently()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        await CheckIndependentDocumentsAsync(provider);
    }

    [Fact]
    public async Task ExistingNamespaceIsRejectedWithoutAddingDocuments()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var template = await TemplateAsync(provider);
        var owner = new SubjectIdentity("owner", "owner", "Owner");
        var original = await new CellBridgeDocumentService(provider).CreateAsync("/existing.docx",
            MinimalDocx.Create(), new(owner, CanCreate: true));

        await Assert.ThrowsAsync<RecoveryConflictException>(() => DocumentLoadSeeder.SeedAsync(provider,
            template, [("/new.docx", owner)]));

        Assert.Equal(original, await provider.State.FindByResourceIdAsync(original!.ResourceId));
        Assert.Null(await provider.State.FindByPathKeyAsync("/NEW.DOCX"));
        Assert.Empty((await ((IProviderRecoveryStore)provider.State).CaptureRecoveryAsync()).Receipts);
    }

    [Fact]
    public async Task AdmissionAndDuplicatePathsFailBeforePublishingAnyState()
    {
        var limits = new StorageLimits { MaxDocuments = 1 };
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore(), limits);
        var template = await TemplateAsync(provider);
        var owner = new SubjectIdentity("owner", "owner", "Owner");
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => DocumentLoadSeeder.SeedAsync(provider,
            template, [("/first.docx", owner), ("/second.docx", owner)]));
        Assert.Empty((await ((IProviderRecoveryStore)provider.State).CaptureRecoveryAsync()).Snapshots);

        var unlimited = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var otherTemplate = await TemplateAsync(unlimited);
        await Assert.ThrowsAsync<ArgumentException>(() => DocumentLoadSeeder.SeedAsync(unlimited,
            otherTemplate, [("/duplicate.docx", owner), ("/DUPLICATE.docx", owner)]));
        Assert.Empty((await ((IProviderRecoveryStore)unlimited.State).CaptureRecoveryAsync()).Snapshots);
    }

    internal static async Task<DocumentState> TemplateAsync(StorageProvider destination)
    {
        // Only state is ephemeral. All graph and package handles are written to the real destination content store.
        var source = new StorageProvider(new InMemoryStateStore(), destination.Content);
        return (await new CellBridgeDocumentService(source).CreateAsync("/template.docx", MinimalDocx.Create("seed"),
            new(new("template-owner", "template", "Template owner"), CanCreate: true)))!;
    }

    internal static async Task CheckIndependentDocumentsAsync(StorageProvider provider)
    {
        var template = await TemplateAsync(provider);
        var owners = Enumerable.Range(0, 3).Select(index =>
            new SubjectIdentity("seed-owner-" + index, "seed-owner-" + index, "Owner " + index)).ToArray();
        var documents = owners.Select((owner, index) => ("/seeded-" + index + ".docx", owner)).ToArray();
        var states = await DocumentLoadSeeder.SeedAsync(provider, template, documents);
        Assert.Equal(3, states.Count);
        Assert.Equal(3, states.Select(state => state.ResourceId).Distinct().Count());
        var inventory = await ((IProviderRecoveryStore)provider.State).CaptureRecoveryAsync();
        Assert.Equal(3, inventory.Heads.Length);
        var receipt = Assert.Single(inventory.Receipts);
        Assert.Equal(3, receipt.Documents);
        Assert.Equal(3, receipt.Snapshots);
        Assert.Equal(64, receipt.Fingerprint.Length);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => DocumentLoadSeeder.SeedAsync(provider,
            template, [("/must-not-appear.docx", owners[0])]));
        Assert.Null(await provider.State.FindByPathKeyAsync("/MUST-NOT-APPEAR.DOCX"));
        var seedBytes = await provider.Content.ReadVerifiedAsync(template.Content);

        var service = new CellBridgeDocumentService(provider);
        for (var index = 0; index < states.Count; index++)
        {
            var initial = states[index];
            var persisted = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
            Assert.Equal(documents[index].Item1, persisted.Path);
            Assert.Equal(StorageIds.PathKey(persisted.Path), persisted.PathKey);
            Assert.Equal(owners[index], persisted.Security.CreatedBy);
            Assert.Equal(owners[index], persisted.Security.ModifiedBy);
            Assert.Equal(owners[index].Subject, persisted.Security.Owner);
            Assert.Empty(persisted.Security.Grants);
            Assert.Equal(0, persisted.StateVersion);
            Assert.Equal(1, persisted.LifecycleGeneration);
            Assert.Empty(persisted.Editors);
            Assert.Empty(persisted.Receipts);
            Assert.Equal(CoordinationState.Empty, persisted.Coordination);
            var revision = Assert.Single(persisted.Revisions);
            Assert.Equal(initial.ResourceId, revision.ResourceId);
            Assert.Equal(owners[index], revision.Author);
            Assert.Equal(initial.Content, revision.Content);
            Assert.Equal(template.Partitions.Select(partition => partition.Identity),
                persisted.Partitions.Select(partition => partition.Identity));
            var restored = await StoredDocument.RestoreAsync(persisted, provider.Content);
            Assert.Equal(seedBytes, restored.FilePartition.FileGraph.Materialize());

            var bytes = MinimalDocx.Create("Independent save " + index);
            var request = SyntheticWorkload.Save(restored, bytes, "changed-full", index, new Random(42 + index));
            var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), new(owners[index]));
            Assert.False(saved.Response.Status);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
            Assert.Equal(initial.ContentVersion + 1, saved.State.ContentVersion);
            Assert.Single(saved.State.Receipts);
            Assert.Equal(bytes, (await StoredDocument.RestoreAsync(saved.State, provider.Content)).Content);
            foreach (var untouched in states.Skip(index + 1))
            {
                var other = (await provider.State.FindByResourceIdAsync(untouched.ResourceId))!;
                Assert.Equal(untouched.ContentVersion, other.ContentVersion);
                Assert.Equal(0, other.StateVersion);
                Assert.Empty(other.Receipts);
            }
        }
    }
}

[Collection("benchmark-database")]
public sealed class PostgreSqlDocumentLoadSeederTests(BenchmarkEnvironment fixture)
{
    [Fact]
    public async Task RecoverySeedsDurableDocumentsThatAcceptIndependentSaves()
    {
        // A new database on the fixture-owned disposable server avoids its shared HTTP document namespace.
        var connection = new NpgsqlConnectionStringBuilder(fixture.Environment.PostgreSqlDocumentConnectionString);
        var databaseName = "cellbridge_load_seed_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(connection.ConnectionString);
        await using (var create = admin.CreateCommand($"CREATE DATABASE {databaseName}"))
            await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = databaseName;
            await using var source = NpgsqlDataSource.Create(connection.ConnectionString);
            var state = new PostgreSqlStateStore(source);
            await state.InitializeAsync();
            var provider = new StorageProvider(state, new PostgreSqlContentStore(source));
            await DocumentLoadSeederTests.CheckIndependentDocumentsAsync(provider);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE {databaseName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
