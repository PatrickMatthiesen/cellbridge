using System.Collections.Immutable;
using System.Security.Cryptography;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>Creates canonical Office graph records without storing a second whole-file projection.</summary>
public static class IndexedDocumentPublicationBuilder
{
    public static async ValueTask<PreparedDocumentCreate> CreateAsync(Guid resourceId, string path, byte[] bytes,
        DocumentSecurity security, IContentStore content, StorageLimits limits, CancellationToken cancellationToken = default,
        long lifecycleGeneration = 1)
    {
        if (resourceId == Guid.Empty) throw new ArgumentException("A nonempty resource identity is required.", nameof(resourceId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lifecycleGeneration);
        StorageLimits.Check("document bytes", bytes.LongLength, limits.MaxDocumentBytes);
        var document = new StoredDocument(path);
        document.SetContent(bytes);
        var graph = document.FilePartition.FileGraph;
        var elements = graph.Elements.ToArray();
        StorageLimits.Check("graph elements", elements.Length, limits.MaxGraphElements);
        StorageLimits.Check("graph bytes", elements.Sum(element => (long)element.Data!.Length), limits.MaxGraphBytes);
        var scope = new IndexedGraphScope(resourceId, lifecycleGeneration, 0);
        var records = elements.Select(element => IndexedDocumentGraph.ReadMetadata(scope, element)).ToArray();
        var descriptors = ImmutableArray.CreateBuilder<GraphElementState>();
        foreach (var element in elements.Where(element => element.DataElementType != DataElementType.StorageIndexDataElementData))
        {
            StorageLimits.Check("graph object bytes", element.Data!.LongLength, limits.MaxObjectBytes);
            using var input = new MemoryStream(element.Data, writable: false);
            var handle = await content.WriteAsync(input, cancellationToken);
            descriptors.Add(new(IndexedDocumentGraph.Capture(element.DataElementExtendedGuid), (uint)element.DataElementType,
                new(element.SerialNumber.Guid, element.SerialNumber.Value), handle)
            {
                MappingSerials = element.DataElementType == DataElementType.StorageIndexDataElementData
                    ? StorageIndexMappingSerials.Read(element.Data).Select(serial => new SerialId(serial.Guid, serial.Value)).ToImmutableArray() : [],
            });
        }
        var index = IndexedDocumentGraph.Capture(graph.StorageIndex);
        var cell = IndexedDocumentGraph.Capture(graph.FileCell);
        var revision = IndexedDocumentGraph.Capture(graph.Revision!);
        var resolved = await IndexedDocumentGraph.ResolveFileCellAsync(scope, index, cell, revision,
            new IndexedGraphOverlaySource(scope, records), limits.GraphTraversalLimits(), cancellationToken);
        var root = new DocumentPackageRoot(scope, index, cell, revision, IndexedDocumentGraph.Capture(graph.RootObject!));
        var package = new DocumentPackage(resourceId, lifecycleGeneration, 1, DocumentPackage.CurrentReconstructionVersion, root,
            bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var partitions = ImmutableArray.CreateBuilder<PreparedPartitionPublication>();
        var storedElements = descriptors.ToImmutable();
        foreach (var partition in new[] { document.FilePartition, document.MetadataPartition, document.EditorsTablePartition })
        {
            var partScope = scope with { TargetPartition = (int)partition.Kind };
            var file = partition.Kind == DocumentPartitionKind.FileContents;
            var head = new PartitionHead((int)partition.Kind, partition.ProtocolIdentity.Capture(), partition.KnowledgeSequence,
                file ? index : null, 1, file ? [] : partition.Content.ToImmutableArray())
            { StorageIndexSerial = file ? new(graph.Elements.Single(element => element.DataElementType == DataElementType.StorageIndexDataElementData).SerialNumber.Guid, graph.Elements.Single(element => element.DataElementType == DataElementType.StorageIndexDataElementData).SerialNumber.Value) : null,
                ElementCount = file ? storedElements.Length : 0, ElementBytes = file ? storedElements.Sum(element => element.Payload.Length) : 0 };
            var bindings = file ? resolved.Bindings : [];
            partitions.Add(new(head with { StorageIndex = null, Knowledge = 0, PublicationSequence = 0, ElementCount = 0, ElementBytes = 0, InlineContent = [] },
                head, file ? storedElements : [], [], file ? records.SelectMany(record => record.Mappings).ToImmutableArray() : [],
                file ? records.Where(record => record.Revision is not null).Select(record => record.Revision!).ToImmutableArray() : [],
                file ? records.SelectMany(record => record.Objects).ToImmutableArray() : [],
                IndexedDocumentGraph.Difference(partScope, 0, 1, [], bindings))
            {
                Checkpoint = IndexedDocumentGraph.Checkpoint(partScope, 1, bindings),
                Cells = file ? records.Where(record => record.CellManifest is not null).Select(record => record.CellManifest!).ToImmutableArray() : [],
                StorageManifests = file ? records.Where(record => record.StorageManifest is not null).Select(record => record.StorageManifest!).ToImmutableArray() : [],
                MappingChanges = file ? records.SelectMany(record => record.Mappings).Select(mapping => new IndexedMappingChange(mapping.Key, new(mapping.Target, mapping.Serial))).ToImmutableArray() : [],
            });
        }
        var prepared = partitions.ToImmutable();
        var author = security.ModifiedBy ?? security.CreatedBy ?? new("unknown", "unknown", "unknown");
        var application = new DocumentRevisionRecord(resourceId, lifecycleGeneration, 1, document.ContentVersion, document.LastModifiedUtc,
            author, package, prepared.Where(partition => partition.After.Kind != 2).Select(partition => partition.After).ToImmutableArray());
        var headRecord = new DocumentHead(resourceId, document.Url, StorageIds.PathKey(document.Url), document.CreatedUtc,
            document.LastModifiedUtc, document.ContentVersion, 0, lifecycleGeneration, false, package, security, 1, new()
            {
                GraphElements = storedElements.Length, GraphBytes = storedElements.Sum(element => element.Payload.Length), Revisions = 1,
                StoredBytes = storedElements.Select(element => element.Payload).DistinctBy(handle => handle.Key).Sum(handle => handle.Length),
            });
        return new(headRecord, CoordinationState.Empty, [], prepared, application);
    }
}
