using System.Collections.Immutable;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed partial class CellBridgeDocumentService
{
    private async ValueTask<CellExecution> QueryPartitionAsync(Guid id, DocumentPartitionKind kind,
        FsshttpbCellSubRequest operation, CellBridgeActor actor, CancellationToken cancellationToken)
    {
        if (kind == DocumentPartitionKind.EditorsTable)
        {
            var controls = await provider.Documents.ReadControlAsync(id, cancellationToken) ?? throw MissingDocument();
            Demand(actor, controls.Head, DocumentAccess.Read);
            if (controls.Editors.Any(editor => editor.ExpiresUtc <= controls.UtcNow))
            {
                var expired = await provider.Documents.ExecuteControlAsync(
                    new DocumentControlCommand.ExpireEditors(id, Admission(actor, controls.Head)), cancellationToken);
                controls = expired.Current;
                Demand(actor, controls.Head, DocumentAccess.Read);
            }
            var document = StoredDocument.RestoreControl(controls);
            var editors = document.EditorsTablePartition;
            return new(ShapeGeneratedPartition(EditorsQuery(document, editors, operation.RequestId, controls.Head), editors,
                operation), controls.Head);
        }
        var request = operation.Data as QueryChangesSubRequestData;
        var read = await provider.Documents.ReadPartitionAsync(id,
            new((int)kind) { IncludeCatalog = true, IncludeBindings = FileQueryResponseBuilder.IsScoped(request) }, cancellationToken) ?? throw MissingDocument();
        Demand(actor, read.Current, DocumentAccess.Read);
        var partition = read.Partition;
        if (partition.StorageIndex is null)
        {
            var generated = new DocumentPartition(partition);
            var generatedResponse = StorageManifestBuilder.BuildStorageIndexOnlyQueryChangesResponse(operation.RequestId,
                new ExGuid(1, generated.ProtocolIdentity.SerialGuid), generated.KnowledgeSequence,
                emitNullManifestMapping: true, includeCellKnowledge: true,
                waterlineCellStorage: generated.FssHttpBIdentity.CellId.ShortId);
            return new(ShapeGeneratedPartition(generatedResponse, generated, operation), read.Current);
        }
        var scope = new IndexedGraphScope(id, read.Current.LifecycleGeneration, (int)kind);
        var indexId = partition.StorageIndex ?? throw new StorageCorruptionException("Missing selected storage index.");
        var mappings = await provider.Documents.ReadMappingsAsync(scope, partition.PublicationSequence, cancellationToken);
        var index = new DataElement(DataElementType.StorageIndexDataElementData, StorageIds.Restore(indexId),
            partition.StorageIndexSerial is { } serial ? new(serial.Guid, serial.Value) : throw new StorageCorruptionException("Missing server index serial."))
        { Data = StorageIndexPatch.Serialize(mappings.Select(RestoreMapping)) };
        var descriptors = read.Elements.ToDictionary(element => element.Id);
        var loaded = new Dictionary<ExtendedId, DataElement> { [indexId] = index };
        var cells = mappings.Where(mapping => mapping.Key.Kind == IndexedMappingKind.Cell && !IsNull(mapping.Target))
            .Select(mapping => mapping.Key.Cell ?? throw new StorageCorruptionException("Missing cell mapping identity."))
            .Select(cell => new CellId(StorageIds.Restore(cell.LongId), StorageIds.Restore(cell.ShortId))).ToArray();
        if (kind == DocumentPartitionKind.FileContents)
        {
            var manifest = mappings.SingleOrDefault(mapping => mapping.Key.Kind == IndexedMappingKind.Manifest && !IsNull(mapping.Target))
                ?? throw new StorageCorruptionException("Missing file storage manifest.");
            cells = PartitionGraphSnapshot.ReadFileCells(index, await ReadElement(scope, manifest.Target, cancellationToken)).ToArray();
        }
        if (!FileQueryResponseBuilder.Supports(request, cells)) return new(FileQueryResponseBuilder.Unsupported(operation.RequestId), read.Current);
        var cell = FileQueryResponseBuilder.IsScoped(request) ? request!.CellId! : cells.FirstOrDefault() ??
            new CellId(StorageIds.Restore(partition.Identity.CellLong), StorageIds.Restore(partition.Identity.CellShort));
        IReadOnlySet<ExGuid>? required = null;
        if (FileQueryResponseBuilder.IsScoped(request))
        {
            var indexedCell = IndexedDocumentGraph.Capture(cell);
            var mapping = mappings.SingleOrDefault(value => value.Key.Kind == IndexedMappingKind.Cell && value.Key.Cell == indexedCell && !IsNull(value.Target));
            if (mapping is null) return new(FileQueryResponseBuilder.Unsupported(operation.RequestId), read.Current);
            var revisionId = IndexedDocumentGraph.Capture(IndexedGraphRecords.ReadCellRevision(await ReadElement(scope, mapping.Target, cancellationToken)));
            var revisionMapping = await provider.Documents.FindMappingAsync(scope, indexId,
                new(IndexedMappingKind.Revision, Revision: revisionId), cancellationToken) ?? throw new StorageCorruptionException("Missing selected revision mapping.");
            var revision = await provider.Documents.ReadRevisionAsync(scope, revisionMapping.Target, cancellationToken)
                ?? throw new StorageCorruptionException("Missing selected revision manifest.");
            var selected = new IndexedResolvedCell(scope, indexedCell, revisionId, revision.Roots,
                read.Bindings.Where(binding => binding.Key.Cell == indexedCell).ToImmutableArray());
            // Save preparation can reuse a resolved base past the generic parser's
            // default depth. Each distinct ancestor needs a catalog element, so the
            // coherent catalog bounds this iterative walk without a second policy cap.
            var queryLimits = provider.Limits.GraphTraversalLimits() with { MaxRevisionDepth = Math.Max(1, read.Elements.Length) };
            required = (await IndexedDocumentGraph.RequiredElementsAsync(scope, indexId, selected, provider.Documents,
                ReadElement, queryLimits, cancellationToken))
                .Select(StorageIds.Restore).ToHashSet();
        }
        var metadata = read.Elements.Select(element => new DataElement((DataElementType)element.Type,
            StorageIds.Restore(element.Id), new(element.Serial.Guid, element.Serial.Value))).Append(index).ToArray();
        var mappingSerials = new Dictionary<ExGuid, IReadOnlyList<SerialNumber>>
        { [index.DataElementExtendedGuid] = mappings.Where(mapping => mapping.Serial.Guid != Guid.Empty)
            .Select(mapping => new SerialNumber(mapping.Serial.Guid, mapping.Serial.Value)).ToArray() };
        var selection = FileQueryResponseBuilder.Select(metadata, index.DataElementExtendedGuid, cell, partition.Knowledge,
            request, mappingSerials, required, StorageIds.Restore(partition.Identity.CellShort));
        var handles = read.Elements.Where(element => selection.PayloadIds.Contains(StorageIds.Restore(element.Id)) && !loaded.ContainsKey(element.Id)).ToArray();
        var bytes = await provider.Content.ReadManyVerifiedAsync(handles.Select(element => element.Payload), provider.Limits.MaxObjectBytes, cancellationToken);
        foreach (var element in handles)
            loaded[element.Id] = new((DataElementType)element.Type, StorageIds.Restore(element.Id), new(element.Serial.Guid, element.Serial.Value))
            { Data = bytes[element.Payload.Key] };
        var response = FileQueryResponseBuilder.Build(operation.RequestId, selection,
            metadata.Where(element => selection.PayloadIds.Contains(element.DataElementExtendedGuid)).Select(element => loaded[StorageIds.Capture(element.DataElementExtendedGuid)]), request);
        var current = await CurrentAsync(id, cancellationToken);
        Demand(actor, current, DocumentAccess.Read);
        if (current.LifecycleGeneration != read.Current.LifecycleGeneration) throw new UnauthorizedAccessException("The document lifecycle changed.");
        return new(response, read.Current);

        async ValueTask<DataElement> ReadElement(IndexedGraphScope selectedScope, ExtendedId elementId, CancellationToken ct)
        {
            if (selectedScope != scope) throw new StorageCorruptionException("Cross-document graph reference.");
            if (loaded.TryGetValue(elementId, out var cached)) return cached;
            if (!descriptors.TryGetValue(elementId, out var element))
                element = (await provider.Documents.ReadElementsAsync(scope, [elementId], ct)).SingleOrDefault()
                    ?? throw new StorageCorruptionException("Missing selected graph element.");
            var value = new DataElement((DataElementType)element.Type, StorageIds.Restore(element.Id), new(element.Serial.Guid, element.Serial.Value))
            { Data = await provider.Content.ReadVerifiedAsync(element.Payload, provider.Limits.MaxObjectBytes, ct) };
            loaded.Add(elementId, value);
            return value;
        }
    }

    private static StorageIndexMapping RestoreMapping(IndexedStorageMapping mapping) => new(
        mapping.Key.Kind == IndexedMappingKind.Manifest ? StreamObjectTypeHeaderStart.StorageIndexManifestMapping :
        mapping.Key.Kind == IndexedMappingKind.Cell ? StreamObjectTypeHeaderStart.StorageIndexCellMapping : StreamObjectTypeHeaderStart.StorageIndexRevisionMapping,
        mapping.Key.Revision is { } revision ? StorageIds.Restore(revision) : null,
        mapping.Key.Cell is { } cell ? new(StorageIds.Restore(cell.LongId), StorageIds.Restore(cell.ShortId)) : null,
        StorageIds.Restore(mapping.Target), new(mapping.Serial.Guid, mapping.Serial.Value));
    private static bool IsNull(ExtendedId id) => id.Guid == Guid.Empty && id.Value == 0;

    private FsshttpbResponse EditorsQuery(StoredDocument document, DocumentPartition partition, ulong requestId, DocumentHead state) =>
        EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(requestId,
            document.Sessions.Select(session => new EditorsTableEditor(session.ClientId.ToString("D"), session.ExpiresUtc.Ticks,
                session.Owner?.DisplayName ?? session.UserName, session.Owner?.Login ?? session.UserName,
                HasEditorPermission: session.AsEditor && session.Owner is not null &&
                    (_externalAuthorization is not null || Access(new(session.Owner), state).HasFlag(DocumentAccess.Write)), Metadata: session.Metadata)),
            partition.FssHttpBIdentity.CellId, partition.FssHttpBIdentity.SerialGuid, partition.KnowledgeSequence);
}
