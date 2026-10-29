using System.Collections.Immutable;
using System.Data.Common;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CellBridge.Tests;

public static class PublicationPrefetchScenario
{
    private static readonly CellBridgeActor Owner = new(new("prefetch:owner", "owner", "Owner"), CanCreate: true);
    private static readonly ImmutableDictionary<string, string> Attributes = ImmutableDictionary<string, string>.Empty;

    public static async Task RunAsync(StorageProvider normal, Func<IInterceptor[], StorageProvider> withInterceptors)
    {
        var commands = new Commands();
        var provider = withInterceptors([commands]);
        var service = new CellBridgeDocumentService(normal);
        var head = (await service.CreateAsync("/prefetch-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create("prefetch initial"), Owner))!;
        var request = LargeMetadata(300);
        var prepared = await PrepareAsync(provider, head, 1, request);
        var part = Assert.Single(prepared.Partitions);
        Assert.True(part.AddedElements.Length > 512);
        Assert.Equal(300, part.EffectiveChanges.Changes.Length);
        // Duplicate claims with another transmission envelope retain the original key/target meaning.
        var duplicate = part.Mappings[0] with { StorageIndex = new(1, Guid.NewGuid()) };
        prepared = prepared with { Partitions = [part with { Mappings = part.Mappings.Add(duplicate) }],
            NewDocumentReferences = prepared.NewDocumentReferences.AddRange(prepared.NewDocumentReferences) };
        var beforeSnapshot = (await normal.Documents.ExportDocumentAsync(head.ResourceId))!;
        var before = SnapshotJson(beforeSnapshot);
        var conflicting = prepared with { Partitions = [prepared.Partitions[0] with
            { Mappings = prepared.Partitions[0].Mappings.Add(duplicate with { Target = new(1, Guid.NewGuid()) }) }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.Documents.PublishAsync(conflicting).AsTask());
        Assert.Equal(before, SnapshotJson((await normal.Documents.ExportDocumentAsync(head.ResourceId))!));

        commands.Clear();
        var accepted = await provider.Documents.PublishAsync(prepared);
        Assert.Equal(PublicationStatus.Accepted, accepted.Status);
        AssertCounts(prepared, commands, beforeSnapshot);
        Assert.All(commands.Observed, command => Assert.InRange(command.Parameters, 1, 1024));
        var snapshot = await NormalizedProviderScenarios.CheckSnapshotAsync(normal.Documents, head.ResourceId);
        Assert.All(snapshot.ContentReferences.Where(reference => prepared.NewDocumentReferences.Contains(reference.Handle)),
            reference => Assert.Equal(1, reference.ReferenceCount));
        // A retry does not reach publication prefetch or increment any reference/counter.
        commands.Clear();
        var replay = await provider.Documents.PublishAsync(prepared with { Admission = Admission(accepted.Current) });
        Assert.Equal(PublicationStatus.Replay, replay.Status);
        Assert.Empty(commands.Observed);
        var inline = await service.RestoreRevisionAsync(head.ResourceId, 1, accepted.Current.LatestRevisionNumber, "prefetch-inline", Owner);
        await NormalizedProviderScenarios.CheckSnapshotAsync(normal.Documents, head.ResourceId);
        await service.RestoreRevisionAsync(head.ResourceId, accepted.Current.LatestRevisionNumber,
            inline.Revision.RevisionNumber, "prefetch-reactivate", Owner);
        await NormalizedProviderScenarios.CheckSnapshotAsync(normal.Documents, head.ResourceId);

        // File history grows while each later request touches the same small number of rows.
        for (var revision = 0; revision < 4; revision++)
        {
            head = (await normal.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
            var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1,
                Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } } };
            var result = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, query, Attributes, Owner);
            var selected = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(result.Response.SubResponses).Data).StorageIndexExtendedGuid;
            var graph = PartitionGraphSnapshot.Create(result.Response.DataElementPackage!.DataElements, selected);
            var save = SyntheticWorkload.Save(graph, MinimalDocx.Create("prefetch revision " + revision), "changed-full", revision, new Random(800 + revision));
            // Re-admit one current immutable object to exercise identity and reference reuse.
            var oldGroup = graph.Elements.First(element => element.DataElementType == DataElementType.ObjectGroupDataElementData);
            save.DataElementPackage!.DataElements.Add(oldGroup);
            var next = await PrepareAsync(provider, head, 0, save);
            beforeSnapshot = (await normal.Documents.ExportDocumentAsync(head.ResourceId))!;
            var file = next.Partitions.Single(value => value.After.Kind == 0);
            var reused = file.AddedElements.Single(element => element.Id == IndexedDocumentGraph.Capture(oldGroup.DataElementExtendedGuid));
            if (revision == 0)
            {
                before = SnapshotJson((await normal.Documents.ExportDocumentAsync(head.ResourceId))!);
                var changed = file with { AddedElements = file.AddedElements.Replace(reused, reused with { Serial = new(Guid.NewGuid(), 1) }) };
                await Assert.ThrowsAsync<InvalidDataException>(() => provider.Documents.PublishAsync(next with
                    { Partitions = next.Partitions.Replace(file, changed) }).AsTask());
                Assert.Equal(before, SnapshotJson((await normal.Documents.ExportDocumentAsync(head.ResourceId))!));
                var missing = new ContentHandle(new string('b', 64), 4, new string('b', 64));
                await Assert.ThrowsAsync<StorageCorruptionException>(() => provider.Documents.PublishAsync(next with
                    { NewDocumentReferences = next.NewDocumentReferences.Add(missing) }).AsTask());
                Assert.Equal(before, SnapshotJson((await normal.Documents.ExportDocumentAsync(head.ResourceId))!));
                using var cancellation = new CancellationTokenSource();
                commands.CancelAtReferences = cancellation;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.Documents.PublishAsync(next, cancellation.Token).AsTask());
                commands.CancelAtReferences = null;
                Assert.Equal(before, SnapshotJson((await normal.Documents.ExportDocumentAsync(head.ResourceId))!));
            }
            commands.Clear();
            accepted = await provider.Documents.PublishAsync(next);
            Assert.Equal(PublicationStatus.Accepted, accepted.Status);
            AssertCounts(next, commands, beforeSnapshot);
            Assert.Equal(7, commands.Observed.Count);
            await NormalizedProviderScenarios.CheckSnapshotAsync(normal.Documents, head.ResourceId);
        }
    }

    private static async Task<PreparedDocumentPublication> PrepareAsync(StorageProvider provider, DocumentHead head, int kind,
        FsshttpbCellRequest request)
    {
        var operation = Assert.Single(request.SubRequests);
        var put = Assert.IsType<PutChangesSubRequestData>(operation.Data);
        var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, kind);
        var keys = IndexedDocumentGraph.ReadMetadata(scope, request.DataElementPackage!.DataElements.Single(element =>
            element.DataElementExtendedGuid.Equals(put.StorageIndex))).Mappings.Select(mapping => mapping.Key).ToImmutableArray();
        var identity = IndexedPartitionSavePreparer.Identify(head.LifecycleGeneration, kind, operation, request.DataElementPackage);
        var read = (await provider.Documents.PrepareSaveAsync(head.ResourceId, new(kind, identity.Operation, keys) { IncludeBindings = true }))!;
        var prepared = await IndexedPartitionSavePreparer.PrepareAsync(read, operation, request.DataElementPackage,
            Admission(read.Control.Head), Attributes, provider.Documents, provider.Content, provider.Limits);
        Assert.False(Assert.Single(prepared.Response.SubResponses).Status, prepared.Response.SubResponses[0].Error?.ErrorMessage);
        return Assert.IsType<PreparedDocumentPublication>(prepared.Publication);
    }

    private static DocumentActorAdmission Admission(DocumentHead head) => new(Owner.Identity, head.ResourceId,
        head.LifecycleGeneration, head.Security.AuthorizationPolicy, DocumentAccess.Read | DocumentAccess.Write)
        { AuthorityGeneration = head.AuthorityGeneration, StateVersion = head.StateVersion };

    private static string SnapshotJson(NormalizedDocumentSnapshot snapshot) => JsonSerializer.Serialize(snapshot with
        { Current = snapshot.Current with { UtcNow = DateTime.UnixEpoch } });

    private static void AssertCounts(PreparedDocumentPublication publication, Commands commands, NormalizedDocumentSnapshot before)
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string key, int count) => expected[key] = expected.GetValueOrDefault(key) + (count + 255) / 256;
        foreach (var part in publication.Partitions)
        {
            Add("elements", part.AddedElements.Select(element => element.Id).Concat(part.RetiredElements)
                .Concat(part.MappingChanges.Where(change => change.Value is { Target: var target } && target != new ExtendedId(0, Guid.Empty))
                    .Select(change => change.Value!.Target)).Distinct().Count());
            Add("visibility", part.AddedElements.Select(element => element.Id).Concat(part.RetiredElements).Distinct().Count());
            Add("graph", part.Revisions.Length + part.Objects.Length + part.Cells.Length + part.StorageManifests.Length);
            Add("bindings", part.EffectiveChanges.Changes.Select(change => change.Key).Distinct().Count());
            Add("claims", part.Mappings.Where(mapping => mapping.Serial.Guid != Guid.Empty || mapping.Serial.Value != 0)
                .Select(mapping => mapping.Serial).Distinct().Count());
        }
        var handles = publication.NewDocumentReferences.Concat(publication.Partitions.SelectMany(part => part.AddedElements).Select(element => element.Payload));
        if (publication.Receipt.Response is { } response) handles = handles.Append(response);
        var references = handles.Select(handle => handle.Key).Distinct().Count();
        Add("physical-content", references); Add("references", references);
        Assert.Equal(expected.Where(pair => pair.Value != 0).OrderBy(pair => pair.Key),
            commands.Observed.GroupBy(command => command.Category).Select(group => KeyValuePair.Create(group.Key, group.Count())).OrderBy(pair => pair.Key));
        var expectedRows = new Dictionary<string, int>(StringComparer.Ordinal);
        void Rows(string category, int count) => expectedRows[category] = expectedRows.GetValueOrDefault(category) + count;
        foreach (var part in publication.Partitions)
        {
            var scope = part.EffectiveChanges.Scope;
            var ids = part.AddedElements.Select(element => element.Id).Concat(part.RetiredElements)
                .Concat(part.MappingChanges.Where(change => change.Value is not null).Select(change => change.Value!.Target)).ToHashSet();
            Rows("elements", before.Elements.Count(element => element.Scope == scope && ids.Contains(element.Element.Id)));
            var visible = part.AddedElements.Select(element => element.Id).Concat(part.RetiredElements).ToHashSet();
            Rows("visibility", before.Visibility.Count(value => value.Scope == scope && value.EndSequence is null && visible.Contains(value.Element)));
            Rows("graph", before.GraphRevisions.Count(value => value.Scope == scope && part.Revisions.Any(requested => requested.ElementId == value.ElementId)) +
                before.Objects.Count(value => value.Scope == scope && part.Objects.Any(requested => requested.GroupElement == value.GroupElement && requested.Ordinal == value.Ordinal)) +
                before.Cells.Count(value => value.Scope == scope && part.Cells.Any(requested => requested.ElementId == value.ElementId)) +
                before.StorageManifests.Count(value => value.Scope == scope && part.StorageManifests.Any(requested => requested.ElementId == value.ElementId)));
            var bindings = part.EffectiveChanges.Changes.Select(value => value.Key).ToHashSet();
            Rows("bindings", before.CurrentBindings.Count(value => bindings.Contains(value.Key)));
            var claims = part.Mappings.Select(value => value.Serial).ToHashSet();
            Rows("claims", before.MappingClaims.Count(value => value.Scope == scope && claims.Contains(value.Serial)));
        }
        var referenceKeys = handles.Select(handle => handle.Key).ToHashSet(StringComparer.Ordinal);
        Rows("references", before.ContentReferences.Count(value => referenceKeys.Contains(value.Handle.Key)));
        Rows("physical-content", references);
        foreach (var (category, queries) in expected)
        {
            var reads = commands.Reads.Where(read => read.Category == category).Sum(read => read.Count);
            // EF counts reader operations; the terminal false read can add one per query.
            Assert.InRange(reads, expectedRows.GetValueOrDefault(category), expectedRows.GetValueOrDefault(category) + queries);
        }
    }

    private static FsshttpbCellRequest LargeMetadata(int count)
    {
        ExGuid Fresh() => new(1, Guid.NewGuid());
        var index = Fresh(); var manifest = Fresh(); var cellManifest = Fresh(); var cell = new CellId(Fresh(), Fresh());
        var root = Fresh();
        var groups = Enumerable.Range(0, count).Select(_ => Fresh()).ToArray();
        var objects = Enumerable.Range(0, count).Select(_ => Fresh()).ToArray();
        var revisions = Enumerable.Range(0, count).Select(_ => Fresh()).ToArray();
        var revisionElements = Enumerable.Range(0, count).Select(_ => Fresh()).ToArray();
        var namespaces = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var mappings = new List<StorageIndexMapping>
        {
            new(StreamObjectTypeHeaderStart.StorageIndexManifestMapping, null, null, manifest, new(namespaces[0], 1)),
            new(StreamObjectTypeHeaderStart.StorageIndexCellMapping, null, cell, cellManifest, new(namespaces[0], 2)),
        };
        var elements = new List<DataElement>
        {
            StorageManifestBuilder.BuildStorageManifestDataElement(manifest, SerialNumber.Null, root, cell),
            StorageManifestBuilder.BuildCellManifestDataElement(cellManifest, SerialNumber.Null, revisions[0]),
        };
        for (var i = 0; i < count; i++)
        {
            mappings.Add(new(StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, revisions[i], null, revisionElements[i], new(namespaces[i % 3], (ulong)i + 10)));
            elements.Add(StorageManifestBuilder.BuildRevisionManifestDataElement(revisionElements[i], SerialNumber.Null, revisions[i],
                ExGuid.Null, root, objects[i], i == 0 ? groups : [groups[i]]));
            elements.Add(StorageManifestBuilder.BuildObjectGroupDataElement(groups[i], SerialNumber.Null, objects[i], 1, [(byte)i]));
        }
        elements.Insert(0, new(DataElementType.StorageIndexDataElementData, index, SerialNumber.Null) { Data = StorageIndexPatch.Serialize(mappings) });
        return new() { DataElementPackage = new() { DataElements = elements }, SubRequests = { new(RequestTypes.PutChanges)
            { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = index, HasAdditionalFlags = true, AdditionalFlagsBits = 1 } } } };
    }

    private sealed class Commands : DbCommandInterceptor
    {
        internal List<(string Category, int Parameters)> Observed { get; } = [];
        internal List<(string Category, int Count)> Reads { get; } = [];
        internal CancellationTokenSource? CancelAtReferences;
        internal void Clear() { Observed.Clear(); Reads.Clear(); }
        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            const string prefix = "-- CellBridge publication prefetch: ";
            if (command.CommandText.StartsWith(prefix, StringComparison.Ordinal))
                Reads.Add((command.CommandText[prefix.Length..].Split('\n')[0].Trim(), eventData.ReadCount));
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            const string prefix = "-- CellBridge publication prefetch: ";
            if (command.CommandText.StartsWith(prefix, StringComparison.Ordinal))
            {
                Assert.NotNull(command.Transaction);
                var category = command.CommandText[prefix.Length..].Split('\n')[0].Trim();
                Observed.Add((category, command.Parameters.Count));
                if (category == "references" && CancelAtReferences is { } cancellation)
                { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            }
            return ValueTask.FromResult(result);
        }
    }
}
