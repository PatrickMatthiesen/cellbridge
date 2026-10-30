using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.FssHttpB;
using CellBridge.TestFixtures;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

internal sealed record SqlServerDocuments(string Admin, string Runtime, StorageLimits Limits)
{
    internal static CellBridgeActor Actor { get; } = new(new("sql:writer", "sql-writer", "SQL writer"), CanCreate: true);
    internal DbContextOptions<SqlServerDocumentDbContext> Options(bool admin = false, params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(admin ? Admin : Runtime,
            sql => sql.MigrationsHistoryTable(SqlServerDocumentDbContext.MigrationsHistoryTable, SqlServerDocumentDbContext.Schema))
            .AddInterceptors(interceptors).Options;

    internal StorageProvider Provider(params IInterceptor[] interceptors)
    {
        var options = Options(false, interceptors);
        var content = new SqlServerContentStore(options, Limits.MaxObjectBytes);
        return new(new SqlServerStateStore(options, Limits, validationContent: content), content, Limits);
    }

    internal static async Task<SqlServerDocuments> CreateAsync(SqlServerFixture fixture, StorageLimits? limits = null)
    {
        var admin = await fixture.CreateDatabaseAsync();
        var configuration = limits ?? new();
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(admin).Options;
        await new SqlServerStorageMigrator(options, configuration).MigrateAsync();
        var name = "cb_runtime_" + Guid.NewGuid().ToString("N");
        var password = "Cb!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var connection = new SqlConnection(admin);
        await connection.OpenAsync();
        await using (var command = new SqlCommand($"CREATE LOGIN [{name}] WITH PASSWORD='{password}'; CREATE USER [{name}] FOR LOGIN [{name}];", connection))
            await command.ExecuteNonQueryAsync();
        foreach (var table in new[] { "documents", "snapshots", "metadata", "revisions", "reservations", "recovery_receipts",
            "repository_heads", "repository_coordination", "repository_editors", "repository_partitions",
            "repository_elements", "repository_visibility", "repository_graph", "repository_bindings",
            "repository_binding_changes", "repository_checkpoints", "repository_revisions", "repository_receipts",
            "repository_paths", "repository_deliveries", "repository_references", "repository_deltas",
            "repository_mappings", "repository_mapping_intervals", "repository_mapping_roots", "repository_mapping_claims" })
        {
            await using var grant = new SqlCommand($"GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.[{table}] TO [{name}]", connection);
            await grant.ExecuteNonQueryAsync();
        }
        await using (var grant = new SqlCommand($"""
            GRANT SELECT,INSERT ON cellbridge_documents.objects TO [{name}];
            GRANT SELECT,INSERT ON cellbridge_documents.chunks TO [{name}];
            GRANT SELECT ON cellbridge_documents.schema_version TO [{name}];
            """, connection)) await grant.ExecuteNonQueryAsync();
        var runtime = new SqlConnectionStringBuilder(admin) { UserID = name, Password = password }.ConnectionString;
        return new(admin, runtime, configuration);
    }

    internal static DocumentActorAdmission Admit(DocumentHead head) => new(Actor.Identity, head.ResourceId,
        head.LifecycleGeneration, head.Security.AuthorizationPolicy, head.Security.AccessFor(Actor.Identity.Subject))
        { AuthorityGeneration = head.AuthorityGeneration, StateVersion = head.StateVersion };

    internal static async Task<FsshttpbCellRequest> ProposalAsync(StorageProvider provider, DocumentHead head, byte[] bytes)
    {
        var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges)
            { RequestId = 1, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } } };
        var result = await new CellBridgeDocumentService(provider).ExecuteAsync(head.ResourceId,
            DocumentPartitionKind.FileContents, query, new Dictionary<string,string>(), Actor);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        var graph = PartitionGraphSnapshot.Create(result.Response.DataElementPackage!.DataElements,
            ((QueryChangesSubResponseData)result.Response.SubResponses[0].Data!).StorageIndexExtendedGuid);
        return SyntheticWorkload.Save(graph, bytes, "changed-full", 0, new Random(Random.Shared.Next()));
    }

    internal static async Task<byte[]> PackageAsync(StorageProvider provider, DocumentPackage package)
    {
        await using var stream = await provider.Packages.OpenReadAsync(package);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    internal static Task<DocumentHead> SaveAsync(StorageProvider provider, DocumentHead head, string text) =>
        SaveAsync(provider, head, MinimalDocx.Create(text));

    internal static async Task<DocumentHead> SaveAsync(StorageProvider provider, DocumentHead head, byte[] bytes)
    {
        var result = await new CellBridgeDocumentService(provider).ExecuteAsync(head.ResourceId,
            DocumentPartitionKind.FileContents, await ProposalAsync(provider, head, bytes),
            new Dictionary<string,string>(), Actor);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.False(Assert.Single(result.AcceptedSaves).IsReplay);
        return result.State;
    }

    internal static async Task<CellExecution> RejectSaveAsync(StorageProvider provider, DocumentHead head, string text)
    {
        var result = await new CellBridgeDocumentService(provider).ExecuteAsync(head.ResourceId,
            DocumentPartitionKind.FileContents, await ProposalAsync(provider, head, MinimalDocx.Create(text)),
            new Dictionary<string,string>(), Actor);
        var failed = Assert.Single(result.Response.SubResponses);
        Assert.True(failed.Status); Assert.NotNull(failed.Error);
        Assert.Equal(ErrorType.Win32, failed.Error.Type);
        Assert.Equal(112UL, failed.Error.ErrorCode);
        Assert.Empty(result.AcceptedSaves);
        return result;
    }

    internal static ValueTask<PathChangeResult> RenameAsync(IDocumentRepository store, DocumentHead head, string path) =>
        store.ChangePathAsync(new(head.ResourceId, Admit(head), path, path.ToUpperInvariant(),
            System.Collections.Immutable.ImmutableDictionary<string,string>.Empty));

    internal static async Task<DocumentHead> DeleteAsync(IDocumentRepository store, Guid id)
    {
        var head = (await store.FindAsync(new DocumentIdentity.Resource(id, true)))!;
        Assert.True((await store.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(id,
            head.LifecycleGeneration, head.StateVersion))).Applied);
        return (await store.FindAsync(new DocumentIdentity.Resource(id, true)))!;
    }

    internal static ValueTask<PreparedDocumentCreate> ReplacementAsync(StorageProvider provider, DocumentHead retired) =>
        IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), retired.Path, MinimalDocx.Create("replacement"),
            retired.Security, provider.Content, provider.Limits, lifecycleGeneration: retired.LifecycleGeneration + 1);

    internal static string SnapshotJson(NormalizedDocumentSnapshot? snapshot) => JsonSerializer.Serialize(snapshot is null ? null :
        snapshot with { Current=snapshot.Current with {UtcNow=default} });
    internal static string SnapshotJson(ProviderSnapshot snapshot) => JsonSerializer.Serialize(snapshot with
        { Snapshots=snapshot.Snapshots.Select(row=>row with { Current=row.Current with {UtcNow=default} }).ToImmutableArray() });

    internal async Task AssertInventoryAsync()
    {
        await new StorageMaintenance(Options(admin: true)).CollectOrphansAsync();
    }
}
