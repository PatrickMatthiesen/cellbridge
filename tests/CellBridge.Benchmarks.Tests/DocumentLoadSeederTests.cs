using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Tests;
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
        var provider = BenchmarkTestDocuments.Memory();
        await CheckIndependentDocumentsAsync(provider);
    }

    [Fact]
    public async Task ExistingNamespaceIsRejectedWithoutAddingDocuments()
    {
        var provider = BenchmarkTestDocuments.Memory();
        var template = await TemplateAsync(provider);
        var owner = new SubjectIdentity("owner", "owner", "Owner");
        var original = await new CellBridgeDocumentService(provider).CreateAsync("/existing.docx",
            MinimalDocx.Create(), new(owner, CanCreate: true));

        await Assert.ThrowsAsync<RecoveryConflictException>(() => DocumentLoadSeeder.SeedAsync(provider,
            template, [("/new.docx", owner)]));

        Assert.Equal(original, await provider.Documents.FindAsync(new DocumentIdentity.Resource(original!.ResourceId)));
        Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Path("/NEW.DOCX")));
        Assert.Empty((await ((IProviderRecoveryStore)provider.Documents).CaptureRecoveryAsync()).Receipts);
    }

    [Fact]
    public async Task DuplicatePathsFailBeforePublishingAnyState()
    {
        var owner = new SubjectIdentity("owner", "owner", "Owner");
        var unlimited = BenchmarkTestDocuments.Memory();
        var otherTemplate = await TemplateAsync(unlimited);
        await Assert.ThrowsAsync<ArgumentException>(() => DocumentLoadSeeder.SeedAsync(unlimited,
            otherTemplate, [("/duplicate.docx", owner), ("/DUPLICATE.docx", owner)]));
        Assert.Empty((await ((IProviderRecoveryStore)unlimited.Documents).CaptureRecoveryAsync()).Snapshots);
    }

    [Fact]
    public async Task SeedingRetainsReactivatedMetadataVisibilityAndExactCounters()
    {
        var destination = BenchmarkTestDocuments.Memory();
        var source = new StorageProvider(new InMemoryDocumentRepository(destination.Content), destination.Content);
        var actor = new CellBridgeActor(new("template", "template", "Template"), CanCreate: true);
        var service = new CellBridgeDocumentService(source);
        var initial = (await service.CreateAsync("/template.docx", MinimalDocx.Create(), actor))!;
        var fixture = new GraphFixture([4, 5, 6], blob: true);
        var upload = new FsshttpbCellRequest { DataElementPackage = new() { DataElements = fixture.Elements.ToList() },
            SubRequests = { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData
                { StorageIndex = fixture.Index, HasAdditionalFlags = true, AdditionalFlagsBits = 1 } } } };
        var opaque = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata, upload,
            new Dictionary<string, string>(), actor);
        Assert.False(Assert.Single(opaque.Response.SubResponses).Status);
        var inline = await service.RestoreRevisionAsync(initial.ResourceId, 1, opaque.State.LatestRevisionNumber, "inline", actor);
        await service.RestoreRevisionAsync(initial.ResourceId, opaque.State.LatestRevisionNumber, inline.Revision.RevisionNumber, "opaque", actor);
        var template = (await source.Documents.ExportDocumentAsync(initial.ResourceId))!;
        Assert.Contains(template.Visibility, value => value.EndSequence is not null);
        var seeded = Assert.Single(await DocumentLoadSeeder.SeedAsync(destination, template, [("/seed.docx", actor.Identity!)]));
        var snapshot = (await destination.Documents.ExportDocumentAsync(seeded.ResourceId))!;
        NormalizedDocumentRecovery.Validate(snapshot, destination.Limits);
        Assert.Equal(NormalizedDocumentRecovery.StoredBytes(snapshot), seeded.Counters.StoredBytes);
        Assert.Equal(template.Visibility.Length, snapshot.Visibility.Length);
        Assert.Equal(template.Elements.Length, snapshot.Elements.Length);
        Assert.Empty(snapshot.Receipts);
        var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } } };
        var result = await new CellBridgeDocumentService(destination).ExecuteAsync(seeded.ResourceId, DocumentPartitionKind.Metadata,
            query, new Dictionary<string, string>(), actor);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        var graph = GenericPartitionGraphSnapshot.Create(result.Response.DataElementPackage!.DataElements,
            Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[0].Data).StorageIndexExtendedGuid);
        Assert.Equal(fixture.Bytes, Assert.Single(graph.GetObjectPartitions(fixture.Cell, fixture.Child)).Content);
    }

    internal static async Task<NormalizedDocumentSnapshot> TemplateAsync(StorageProvider destination)
    {
        // Only state is ephemeral. All graph and package handles are written to the real destination content store.
        var source = new StorageProvider(new InMemoryDocumentRepository(destination.Content), destination.Content);
        var actor = new CellBridgeActor(new("template-owner", "template", "Template owner"), CanCreate: true);
        var service = new CellBridgeDocumentService(source);
        var head = (await service.CreateAsync("/template.docx", MinimalDocx.Create("initial"), actor))!;
        var graph = await BenchmarkTestDocuments.GraphAsync(service, head, actor);
        var saved = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
            SyntheticWorkload.Save(graph, MinimalDocx.Create("seed"), "changed-full", 1, new Random(7184)),
            new Dictionary<string, string>(), actor);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var template = (await source.Documents.ExportDocumentAsync(head.ResourceId))!;
        Assert.Equal(2, template.Current.Partitions.Single(partition => partition.Kind == 1).PublicationSequence);
        Assert.NotEmpty(template.Receipts);
        return template;
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
        var inventory = await ((IProviderRecoveryStore)provider.Documents).CaptureRecoveryAsync();
        Assert.Equal(3, inventory.Heads.Length);
        var receipt = Assert.Single(inventory.Receipts);
        Assert.Equal(3, receipt.Documents);
        Assert.Equal(3, receipt.Snapshots);
        Assert.Equal(64, receipt.Fingerprint.Length);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => DocumentLoadSeeder.SeedAsync(provider,
            template, [("/must-not-appear.docx", owners[0])]));
        Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Path("/MUST-NOT-APPEAR.DOCX")));
        var seedBytes = await BenchmarkTestDocuments.PackageAsync(provider, states[0].Package);

        var service = new CellBridgeDocumentService(provider);
        for (var index = 0; index < states.Count; index++)
        {
            var initial = states[index];
            var persisted = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
            Assert.Equal(documents[index].Item1, persisted.Path);
            Assert.Equal(StorageIds.PathKey(persisted.Path), persisted.PathKey);
            Assert.Equal(owners[index], persisted.Security.CreatedBy);
            Assert.Equal(owners[index], persisted.Security.ModifiedBy);
            Assert.Equal(owners[index].Subject, persisted.Security.Owner);
            Assert.Empty(persisted.Security.Grants);
            Assert.Equal(0, persisted.StateVersion);
            Assert.Equal(1, persisted.LifecycleGeneration);
            var control = (await provider.Documents.ReadControlAsync(persisted.ResourceId))!;
            Assert.Empty(control.Editors);
            Assert.Equal(0, persisted.Counters.SaveReceipts);
            var exported = (await provider.Documents.ExportDocumentAsync(persisted.ResourceId))!;
            NormalizedDocumentRecovery.Validate(exported, provider.Limits);
            Assert.Equal(NormalizedDocumentRecovery.StoredBytes(exported), persisted.Counters.StoredBytes);
            Assert.Equal(template.Visibility.Length, exported.Visibility.Length);
            Assert.Equal(template.Elements.Length, persisted.Counters.GraphElements);
            Assert.Equal(template.Elements.Sum(element => element.Element.Payload.Length), persisted.Counters.GraphBytes);
            Assert.Equal(CoordinationState.Empty, control.Coordination);
            var revision = Assert.Single((await provider.Documents.ReadRevisionsAsync(persisted.ResourceId, 0, 10))!.Revisions);
            Assert.Equal(initial.ResourceId, revision.ResourceId);
            Assert.Equal(owners[index], revision.Author);
            Assert.Equal(initial.Package, revision.Package);
            Assert.Equal(template.Partitions.Select(partition => partition.Head.Identity),
                control.Partitions.Select(partition => partition.Identity));
            var restored = await BenchmarkTestDocuments.GraphAsync(service, persisted, new(owners[index]));
            Assert.Equal(seedBytes, restored.Materialize());

            var bytes = MinimalDocx.Create("Independent save " + index);
            var request = SyntheticWorkload.Save(restored, bytes, "changed-full", index, new Random(42 + index));
            var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), new(owners[index]));
            Assert.False(saved.Response.Status);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
            Assert.Equal(initial.ContentVersion + 1, saved.State.ContentVersion);
            Assert.Equal(1, saved.State.Counters.SaveReceipts);
            Assert.Equal(bytes, await BenchmarkTestDocuments.PackageAsync(provider, saved.State.Package));
            foreach (var untouched in states.Skip(index + 1))
            {
                var other = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(untouched.ResourceId)))!;
                Assert.Equal(untouched.ContentVersion, other.ContentVersion);
                Assert.Equal(0, other.StateVersion);
                Assert.Equal(0, other.Counters.SaveReceipts);
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
