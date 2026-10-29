using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Tests;

/// <summary>Offline pre-rewrite input for real schema-upgrade tests.</summary>
public static class NormalizedLegacyFixture
{
    public static async Task<ImmutableArray<DocumentState>> CreateAsync(IContentStore content, bool renameBack = false, bool metadataReceipts = false)
    {
        var actor = new SubjectIdentity("legacy:owner", "owner", "Legacy owner");
        var document = new DocumentStore().Put("/legacy.docx", MinimalDocx.Create("legacy original"));
        document.Security = DocumentSecurity.Create(actor);
        var state = RevisionHistory.Initialize(await document.CaptureAsync(content));
        var snapshots = ImmutableArray.CreateBuilder<DocumentState>(); snapshots.Add(state);
        for (var iteration = 0; iteration < 2; iteration++)
        {
            document = await StoredDocument.RestoreAsync(state, content);
            var bytes = MinimalDocx.Create("legacy revision " + iteration);
            var request = SyntheticWorkload.Save(document.FilePartition.FileGraph, bytes, "changed-full", iteration + 1, new Random(iteration + 10));
            var operation = request.SubRequests.Single();
            var put = (PutChangesSubRequestData)operation.Data!;
            var graph = PartitionGraphSnapshot.Create(request.DataElementPackage!.DataElements, put.StorageIndex);
            if (!graph.Materialize().SequenceEqual(bytes)) throw new InvalidOperationException("Legacy test graph does not reproduce its package.");
            document.CommitFileRevision(graph, bytes, document.FilePartition.KnowledgeSequence + 1);
            var response = new FsshttpbResponse();
            using var responseBytes = new MemoryStream(response.ToByteArray());
            var responseHandle = await content.WriteAsync(responseBytes);
            var identity = IndexedPartitionSavePreparer.Identify(state.LifecycleGeneration, 0, operation, request.DataElementPackage!);
            var receipts = state.Receipts.Add(new(identity.Operation.OperationKey, identity.Digest, document.ContentVersion, responseHandle,
                iteration == 0 ? null : actor.Subject));
            var next = await document.CaptureAsync(content, receipts: receipts);
            state = RevisionHistory.Append(state, next with { StateVersion = state.StateVersion + 1 }, actor, next.ModifiedUtc);
            snapshots.Add(state);
        }
        var now = DateTime.UtcNow;
        state = state with
        {
            StateVersion = state.StateVersion + 1,
            RetiredPathKeys = renameBack ? [] : [StorageIds.PathKey("/legacy-retired.docx")],
            Coordination = state.Coordination with { HostLock = new("legacy-host", actor.Subject, now.AddMinutes(20)),
                Exclusive = new("legacy-lock", "legacy-client", now.AddMinutes(10), 0, null, actor.Subject), Generation = 3 },
            Editors = [new(Guid.NewGuid(), "Legacy editor", now, now, now.AddMinutes(10), 600, true, 1,
                ImmutableDictionary<string, ImmutableArray<byte>>.Empty, actor)],
        };
        snapshots.Add(state);
        if (renameBack)
        {
            // Legacy rename accumulates old paths even when one becomes current again.
            var originalPath = state.Path;
            var originalKey = state.PathKey;
            state = state with { Path = "/legacy-renamed.docx", PathKey = StorageIds.PathKey("/legacy-renamed.docx"),
                StateVersion = state.StateVersion + 1, RetiredPathKeys = [originalKey] };
            snapshots.Add(state);
            state = state with { Path = originalPath, PathKey = originalKey,
                StateVersion = state.StateVersion + 1, RetiredPathKeys = [originalKey, state.PathKey] };
            snapshots.Add(state);
        }
        if (metadataReceipts)
            for (var version = 0; version < 2; version++)
            {
                var request = MetadataSave(version);
                var metadata = state.Partitions.Single(partition => partition.Kind == 1);
                var elements = ImmutableArray.CreateBuilder<GraphElementState>();
                foreach (var element in request.DataElementPackage!.DataElements)
                    elements.Add(new(IndexedDocumentGraph.Capture(element.DataElementExtendedGuid), (uint)element.DataElementType,
                        new(element.SerialNumber.Guid, element.SerialNumber.Value), await content.WriteAsync(new MemoryStream(element.Data!)))
                        { MappingSerials = element.DataElementType == DataElementType.StorageIndexDataElementData
                            ? StorageIndexMappingSerials.Read(element.Data!).Select(serial => new SerialId(serial.Guid, serial.Value)).ToImmutableArray() : [] });
                var index = ((PutChangesSubRequestData)request.SubRequests[0].Data!).StorageIndex;
                metadata = metadata with { StorageIndex = IndexedDocumentGraph.Capture(index), Elements = elements.ToImmutable(),
                    Knowledge = metadata.Knowledge + 1, InlineContent = [] };
                var response = new FsshttpbResponse { SubResponses = { new() { RequestId = 1, RequestType = RequestTypes.PutChanges,
                    Data = new PutChangesSubResponseData { PutChangesResponse = new() { AppliedStorageIndexID = index },
                        KnowledgeBytes = BinaryKnowledgeBuilder.FromRanges(elements.Select(element =>
                            (element.Serial.Guid, element.Serial.Value, element.Serial.Value)),
                            IndexedDocumentGraph.Restore(metadata.Identity.CellShort), 0) } } } };
                var identity = IndexedPartitionSavePreparer.Identify(state.LifecycleGeneration, 1, request.SubRequests[0], request.DataElementPackage!);
                var receipt = new SaveReceipt(identity.Operation.OperationKey, identity.Digest, state.ContentVersion,
                    await content.WriteAsync(new MemoryStream(response.ToByteArray())), actor.Subject)
                    { PartitionKind = 1, AcceptedStorageIndex = metadata.StorageIndex, LifecycleGeneration = state.LifecycleGeneration };
                state = RevisionHistory.Append(state, state with { StateVersion = state.StateVersion + 1,
                    Partitions = state.Partitions.Select(partition => partition.Kind == 1 ? metadata : partition).ToImmutableArray(),
                    Receipts = state.Receipts.Add(receipt) }, actor, DateTime.UtcNow);
                snapshots.Add(state);
            }
        return snapshots.ToImmutable();
    }
    public static FsshttpbCellRequest MetadataSave(int version)
    {
        var space = version == 0 ? Guid.Parse("380c62e0-4cf5-4c48-839c-c6ae093778f0") : Guid.Parse("380c62e0-4cf5-4c48-839c-c6ae093778f1");
        var graph = new GraphFixture([(byte)version, 2, 3], blob: true, identity: value => new(value, space));
        var request = new FsshttpbCellRequest { DataElementPackage = new() };
        // The first save maps an extra revision; the next explicitly clears it.
        // The nil target still carries knowledge that a reconnecting client needs.
        var writer = new BinaryWriterEx();
        writer.WriteBytes(graph.Elements.Single(element => element.DataElementType == DataElementType.StorageIndexDataElementData).Data!);
        GraphFixture.Record(writer, StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, body =>
        {
            new ExGuid(500, Guid.Parse("bf02d933-55bb-4c5d-b378-e5ddd9377a41")).Serialize(body);
            (version == 0 ? graph.RevisionElement : ExGuid.Null).Serialize(body);
            new SerialNumber(space, 500).Serialize(body);
        });
        graph.Replace(new DataElement(DataElementType.StorageIndexDataElementData, graph.Index,
            graph.Elements.Single(element => element.DataElementType == DataElementType.StorageIndexDataElementData).SerialNumber)
            { Data = writer.ToArray() });
        request.DataElementPackage.DataElements.AddRange(graph.Elements);
        request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = 1,
            Data = new PutChangesSubRequestData { StorageIndex = graph.Index } });
        return request;
    }
    public static async Task VerifyAsync(StorageProvider provider, IContentStore originalContent, ImmutableArray<DocumentState> legacy)
    {
        var original = legacy[^1];
        var exported = await NormalizedProviderScenarios.CheckSnapshotAsync(provider.Documents, original.ResourceId);
        if (exported.ApplicationRevisions.Length != original.Revisions.Length || exported.Receipts.Length != original.Receipts.Length ||
            exported.Current.Coordination != original.Coordination && System.Text.Json.JsonSerializer.Serialize(exported.Current.Coordination) != System.Text.Json.JsonSerializer.Serialize(original.Coordination) ||
            exported.Current.Editors.Length != original.Editors.Length || !exported.PathClaims.Contains(original.RetiredPathKeys[0]) ||
            exported.Receipts.Count(receipt => receipt.OwnerSubject is null) != 1)
            throw new InvalidOperationException("Migration lost history, actorless receipts, active coordination, editors or retired paths.");
        var expectedPaths = original.RetiredPathKeys.Append(original.PathKey).ToHashSet(StringComparer.Ordinal);
        if (!expectedPaths.SetEquals(exported.PathClaims) || exported.Current.Head.Counters.RetiredPaths != expectedPaths.Count - 1 ||
            exported.Current.Head.Counters.StoredBytes != NormalizedDocumentRecovery.StoredBytes(exported))
            throw new InvalidOperationException("Migration duplicated path claims or their accounting.");
        await provider.Documents.CaptureRecoveryAsync();
        if (original.Receipts.Any(receipt => receipt.PartitionKind == 1))
        {
            foreach (var receipt in exported.Receipts.Where(receipt => receipt.Operation.PartitionKind == 1))
                if (!exported.MappingRoots.Any(root => root.Scope.TargetPartition == 1 && root.Id == receipt.AcceptedStorageIndex))
                    throw new InvalidOperationException("A migrated metadata receipt has no retained logical root.");
            var service = new CellBridgeDocumentService(provider);
            var owner = new CellBridgeActor(original.Security.CreatedBy!, CanCreate: true);
            var replay = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.Metadata, MetadataSave(1),
                ImmutableDictionary<string, string>.Empty, owner);
            if (replay.LockError is not null || replay.Response.SubResponses.Count != 1 || replay.Response.SubResponses[0].Status)
                throw new InvalidOperationException("The current legacy metadata receipt did not replay after migration.");
            var accepted = original.Receipts.Last(receipt => receipt.PartitionKind == 1);
            if (!(await originalContent.ReadVerifiedAsync(accepted.Response!)).SequenceEqual(replay.Response.ToByteArray()))
                throw new InvalidOperationException("Migration changed the accepted metadata reply.");
            var stale = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.Metadata, MetadataSave(0),
                ImmutableDictionary<string, string>.Empty, owner);
            if (stale.Response.SubResponses.Count != 1 || !stale.Response.SubResponses[0].Status ||
                stale.Response.SubResponses[0].Error?.ErrorCode != (ulong)CellErrorCode.CoherencyFailure)
                throw new InvalidOperationException("A superseded legacy metadata receipt was incorrectly replayed.");
            var unchanged = (await NormalizedProviderScenarios.CheckSnapshotAsync(provider.Documents, original.ResourceId)).Current.Head;
            if (System.Text.Json.JsonSerializer.Serialize(unchanged) != System.Text.Json.JsonSerializer.Serialize(exported.Current.Head))
                throw new InvalidOperationException("Metadata replay published another revision.");
        }
        // A warm client knows the legacy index bytes, not the freshly generated logical index.
        foreach (var partition in original.Partitions.Where(partition => partition.StorageIndex is not null))
        {
            var serials = partition.Elements.Select(element => element.Serial)
                .Concat(partition.Elements.SelectMany(element => element.MappingSerials));
            var knowledge = ClientKnowledge.Deserialize(new BinaryReaderEx(BinaryKnowledgeBuilder.FromRanges(
                serials.Select(serial => (serial.Guid, serial.Value, serial.Value)), ExGuid.Null, 0)));
            var query = new FsshttpbCellRequest();
            query.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1,
                Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge } });
            var response = await new CellBridgeDocumentService(provider).ExecuteAsync(original.ResourceId,
                (DocumentPartitionKind)partition.Kind, query, ImmutableDictionary<string, string>.Empty,
                new CellBridgeActor(original.Security.CreatedBy!, CanCreate: true));
            var migrated = exported.Current.Partitions.Single(value => value.Kind == partition.Kind);
            var index = response.Response.DataElementPackage?.DataElements.SingleOrDefault(element =>
                IndexedDocumentGraph.Capture(element.DataElementExtendedGuid) == migrated.StorageIndex);
            if (response.Response.SubResponses.Any(value => value.Status) || index is null || knowledge.Contains(index.SerialNumber))
                throw new InvalidOperationException("A warm legacy client did not receive its new logical storage index.");
            var legacyIndex = partition.Elements.Single(element => element.Id == partition.StorageIndex);
            var expectedMappings = StorageIndexMappingSerials.ReadMappings(await originalContent.ReadVerifiedAsync(legacyIndex.Payload));
            var actualMappings = StorageIndexMappingSerials.ReadMappings(index.Data!);
            static IEnumerable<string> CanonicalMappings(IEnumerable<StorageIndexMapping> mappings) =>
                mappings.Select(mapping => Convert.ToHexString(StorageIndexPatch.Serialize([mapping]))).Order(StringComparer.Ordinal);
            if (!CanonicalMappings(expectedMappings).SequenceEqual(CanonicalMappings(actualMappings)))
                throw new InvalidOperationException("Migration changed a storage-index mapping, including its nil target or serial.");
        }
        foreach (var revision in exported.ApplicationRevisions)
        {
            await using var download = await provider.Packages.OpenReadAsync(revision.Package);
            using var output = new MemoryStream(); await download.CopyToAsync(output);
            var old = original.Revisions.Single(record => record.RevisionNumber == revision.RevisionNumber);
            if (!(await originalContent.ReadVerifiedAsync(old.Content)).SequenceEqual(output.ToArray()))
                throw new InvalidOperationException("Migrated historical package differs.");
        }
    }
}
