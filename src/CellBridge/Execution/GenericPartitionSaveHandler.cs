using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>Prepares opaque application metadata without invoking the file adapter.</summary>
internal static class GenericPartitionSaveHandler
{
    internal sealed record Prepared(FsshttpbResponse Response, GenericPartitionGraphSnapshot? Graph = null,
        ulong KnowledgeSequence = 0);

    internal static Prepared Prepare(PartitionState source, IReadOnlyCollection<DataElement> retained,
        FsshttpbCellSubRequest operation, DataElementPackage package, StorageLimits limits)
    {
        var put = (PutChangesSubRequestData)operation.Data!;
        try
        {
            var incoming = new Dictionary<ExGuid, DataElement>();
            foreach (var element in package.DataElements)
            {
                if (element.DataElementExtendedGuid.IsNull || element.Data is null)
                    throw new InvalidDataException("Graph data elements require an identifier and payload.");
                if (incoming.TryGetValue(element.DataElementExtendedGuid, out var duplicate) &&
                    (duplicate.DataElementType != element.DataElementType || !duplicate.SerialNumber.Equals(element.SerialNumber) ||
                     !duplicate.Data!.SequenceEqual(element.Data)))
                    throw new InvalidDataException("Conflicting duplicate graph elements.");
                incoming[element.DataElementExtendedGuid] = element;
            }
            if (!incoming.TryGetValue(put.StorageIndex, out var proposed))
                return Reject(operation.RequestId, CellErrorCode.ReferencedDataElementNotFound, "Missing uploaded storage index.");
            var updates = StorageIndexPatch.Read(proposed);
            var elements = retained.ToDictionary(e => e.DataElementExtendedGuid);
            var current = source.StorageIndex is null ? [] : StorageIndexPatch.Read(elements[StorageIds.Restore(source.StorageIndex)]);
            IReadOnlyList<StorageIndexMapping> expected = [];
            if (!put.ExpectedStorageIndex.IsNull)
            {
                if (!incoming.TryGetValue(put.ExpectedStorageIndex, out var expectedElement))
                    return Reject(operation.RequestId, CellErrorCode.ReferencedDataElementNotFound, "Missing expected storage index in request package.");
                expected = StorageIndexPatch.Read(expectedElement);
            }
            if (!StorageIndexPatch.IsCoherent(current, updates, expected, (put.Flags & 1) != 0))
                return Reject(operation.RequestId, CellErrorCode.CoherencyFailure, "An updated storage-index mapping is no longer coherent.");

            var meanings = new Dictionary<SerialNumber, StorageIndexMapping>();
            foreach (var mapping in retained.Concat(incoming.Values).Where(e => e.DataElementType == DataElementType.StorageIndexDataElementData)
                         .SelectMany(e => StorageIndexPatch.Read(e)).Where(m => !m.Serial.IsNull))
            {
                if (meanings.TryGetValue(mapping.Serial, out var previous) && previous != mapping)
                    throw new InvalidDataException("A mapping serial was reused for a different mapping.");
                meanings[mapping.Serial] = mapping;
            }
            ulong sequence = source.Knowledge;
            var serialGuid = source.Identity.SerialGuid;
            foreach (var serial in retained.Concat(incoming.Values).Select(e => e.SerialNumber).Concat(meanings.Keys))
                if (serial.Guid == serialGuid) sequence = Math.Max(sequence, serial.Value);
            var added = new List<ExGuid>();
            var admitted = new List<DataElement>();
            foreach (var element in incoming.Values)
            {
                StorageLimits.Check("graph object bytes", element.Data!.LongLength, limits.MaxObjectBytes);
                SerialNumber serial;
                if (elements.TryGetValue(element.DataElementExtendedGuid, out var previous))
                {
                    if (previous.DataElementType != element.DataElementType || !previous.Data!.SequenceEqual(element.Data))
                        throw new InvalidDataException("An immutable graph identifier was reused for different content.");
                    serial = previous.SerialNumber;
                }
                else
                {
                    serial = new(serialGuid, checked(++sequence));
                    added.Add(element.DataElementExtendedGuid);
                }
                var accepted = new DataElement(element.DataElementType, element.DataElementExtendedGuid, serial) { Data = element.Data.ToArray() };
                elements[element.DataElementExtendedGuid] = accepted;
                admitted.Add(accepted);
            }
            var merged = StorageIndexPatch.Merge(current, updates);
            var selected = elements[put.StorageIndex];
            if (!merged.SequenceEqual(proposed.Data!))
            {
                selected = new(DataElementType.StorageIndexDataElementData, new(1, Guid.NewGuid()), new(serialGuid, checked(++sequence))) { Data = merged };
                elements.Add(selected.DataElementExtendedGuid, selected);
            }
            StorageLimits.Check("graph elements", elements.Count, limits.MaxGraphElements);
            StorageLimits.Check("graph bytes", elements.Values.Sum(e => e.Data!.LongLength), limits.MaxGraphBytes);
            var graph = GenericPartitionGraphSnapshot.Create(elements.Values, selected.DataElementExtendedGuid,
                new GenericGraphLimits(MaxElements: limits.MaxGraphElements, MaxPayloadBytes: limits.MaxGraphBytes));
            var closure = graph.RequiredElements.ToHashSet();
            if (added.Any(id => elements[id].DataElementType != DataElementType.StorageIndexDataElementData && !closure.Contains(id)))
                throw new InvalidDataException("A complete metadata upload contains unrooted new graph elements.");
            var applied = new PutChangesResponse { AppliedStorageIndexID = (put.AdditionalFlagsBits & 1) != 0 ? put.StorageIndex : ExGuid.Null };
            applied.DataElementAdded.AddRange(added);
            var response = new FsshttpbResponse
            {
                SubResponses = { new() { RequestId = operation.RequestId, RequestType = RequestTypes.PutChanges,
                    Data = new PutChangesSubResponseData
                    {
                        PutChangesResponse = applied,
                        SerialNumberReassignAllBytes = FilePartitionSaveHandler.SerialReassignments(admitted),
                        KnowledgeBytes = BinaryKnowledgeBuilder.FromElements(graph.Elements,
                            StorageIds.Restore(source.Identity.CellShort), sequence,
                            mappingSerials: graph.MappingSerials.Values.SelectMany(s => s)),
                    } } },
            };
            if ((put.AdditionalFlagsBits & 1) != 0)
            {
                response.DataElementPackage = new();
                response.DataElementPackage.DataElements.Add(elements[put.StorageIndex]);
            }
            return new(response, graph, sequence);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
        { return Reject(operation.RequestId, CellErrorCode.InvalidObject, ex.Message); }
    }

    private static Prepared Reject(ulong id, CellErrorCode error, string message) => new(new()
    { SubResponses = { new() { RequestId = id, RequestType = RequestTypes.PutChanges, Status = true,
        Error = new(ErrorType.Cell, (ulong)error, message) } } });
}
