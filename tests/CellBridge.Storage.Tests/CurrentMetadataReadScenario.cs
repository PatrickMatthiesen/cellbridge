using System.Collections.Immutable;
using System.Data.Common;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.Tests;

public static class CurrentMetadataReadScenario
{
    public static async Task RunAsync(StorageProvider provider, Func<IInterceptor[], IDocumentRepository> observed, Action<string> output)
    {
        var actor = new CellBridgeActor(new("metadata:owner", "metadata-owner", "Metadata owner"), CanCreate: true);
        var head = (await new CellBridgeDocumentService(provider).CreateAsync("/metadata-reads.docx", MinimalDocx.Create("mapping counts"), actor))!;
        var control = (await provider.Documents.ReadControlAsync(head.ResourceId))!;
        var partition = control.Partitions.Single(p => p.Kind == 0);
        var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, 0);
        var existing = await provider.Documents.ReadMappingsAsync(scope, partition.PublicationSequence);
        var affected = existing.Select(m => m.Key).Concat(Enumerable.Range(0, 257).Select(_ =>
            new IndexedMappingKey(IndexedMappingKind.Revision, Revision: new(1, Guid.NewGuid())))).ToImmutableArray();
        var audit = new Commands(); var repository = observed([audit]);
        var operation = new SaveOperationIdentity(head.LifecycleGeneration, SaveOperationFamily.Protocol, 0, "metadata-fence");
        var prepared = (await repository.PrepareSaveAsync(head.ResourceId, new(0, operation, affected) { IncludeBindings = true }))!;
        Assert.Equal(existing.Length, prepared.Partition.Mappings.Length);
        Assert.All(prepared.Partition.Mappings, mapping => Assert.Equal(partition.StorageIndex, mapping.StorageIndex));
        var prepare = audit.Counts(); output("Prepare: " + prepare);
        audit.Items.Clear();
        var admission = new DocumentActorAdmission(actor.Identity, head.ResourceId, head.LifecycleGeneration,
            head.Security.AuthorizationPolicy, head.Security.AccessFor(actor.Identity.Subject))
            { AuthorityGeneration = head.AuthorityGeneration, StateVersion = head.StateVersion };
        var expected = existing.ToDictionary(mapping => mapping.Key);
        var publication = new PreparedDocumentPublication(head.ResourceId, admission,
            new(head.LifecycleGeneration, head.AuthorityGeneration, control.Coordination.Generation, head.StateVersion, head.ContentVersion,
                affected.Select(key => new MappingExpectation(key, expected.GetValueOrDefault(key))).ToImmutableArray()),
            new(operation, "current-metadata-fence", actor.Identity.Subject, head.ContentVersion, head.LatestRevisionNumber, partition.StorageIndex, null), []);
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(publication)).Status);
        var fence = audit.Counts(); output("Fence: " + fence);
        Assert.Equal(1, prepare.Partitions);
        Assert.Equal(1, prepare.Roots);
        Assert.Equal((affected.Length + 255) / 256, prepare.CurrentMappings);
        Assert.Equal(0, prepare.Intervals);
        Assert.Equal(1, prepare.Bindings);
        Assert.Equal(0, prepare.CatalogOrHistory);
        Assert.Equal(1, fence.Roots);
        Assert.Equal((affected.Length + 255) / 256, fence.CurrentMappings);
        Assert.Equal(0, fence.Intervals);
        Assert.Equal(0, fence.CatalogOrHistory);
    }

    private sealed class Commands : DbCommandInterceptor
    {
        public List<string> Items { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || command.CommandText.TrimStart().StartsWith("--", StringComparison.Ordinal)) Items.Add(command.CommandText); return ValueTask.FromResult(result); }
        public Counts Counts() => new(
            Items.Count(sql => sql.Contains("repository_partitions", StringComparison.Ordinal)),
            Items.Count(sql => sql.Contains("repository_mapping_roots", StringComparison.Ordinal)),
            Items.Count(sql => sql.Contains("repository_mappings", StringComparison.Ordinal)),
            Items.Count(sql => sql.Contains("repository_mapping_intervals", StringComparison.Ordinal)),
            Items.Count(sql => sql.Contains("repository_bindings", StringComparison.Ordinal)),
            Items.Count(sql => new[] { "repository_elements", "repository_visibility", "repository_revisions", "repository_graph", "repository_checkpoints", "repository_binding_changes", "repository_deltas" }
                .Any(table => sql.Contains(table, StringComparison.Ordinal))));
    }
    private sealed record Counts(int Partitions, int Roots, int CurrentMappings, int Intervals, int Bindings, int CatalogOrHistory);
}
