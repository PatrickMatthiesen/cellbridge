using CellBridge.FssHttpB;

namespace CellBridge.AspNetCore;

internal sealed record FileQuerySelection(HashSet<ExGuid> PayloadIds, QueryChangesSubResponseData Data,
    string? Error = null);

internal static class FileQueryResponseBuilder
{
    public static bool Supports(QueryChangesSubRequestData? request, CellId cell) =>
        Supports(request, new[] { cell });

    public static bool IsScoped(QueryChangesSubRequestData? request) => request?.CellId is { } cell &&
        !(cell.LongId.IsNull && cell.ShortId.IsNull);

    public static bool Supports(QueryChangesSubRequestData? request, IReadOnlyCollection<CellId> cells) =>
        request is null || (!request.HasUnsupportedQueryControls && request.Waterline is null &&
        request.StorageManifestRoot is null && (!IsScoped(request) || cells.Contains(request.CellId!)));

    public static FileQuerySelection Select(IEnumerable<DataElement> elements, ExGuid index,
        CellId cell, ulong sequence, QueryChangesSubRequestData? request,
        IReadOnlyDictionary<ExGuid, IReadOnlyList<SerialNumber>>? mappingSerials = null,
        IReadOnlySet<ExGuid>? scope = null, ExGuid? cellStorageId = null,
        IReadOnlyDictionary<ExGuid, long>? payloadLengths = null)
    {
        var metadata = elements.ToArray();
        mappingSerials ??= new Dictionary<ExGuid, IReadOnlyList<SerialNumber>>();
        // Client graphs can alias DE serials to each other or to mappings.
        // Those serials cannot prove possession of an individual payload.
        var ambiguous = metadata.Where(e => !e.SerialNumber.IsNull).GroupBy(e => e.SerialNumber)
            .Where(g => g.Select(e => e.DataElementExtendedGuid).Distinct().Count() > 1)
            .Select(g => g.Key).Concat(mappingSerials.Values.SelectMany(s => s)).ToHashSet();
        bool Visible(DataElement e) => request is null ||
            (request.IncludeStorageManifest || e.DataElementType != DataElementType.StorageManifestDataElementData) &&
            (request.IncludeCellChanges || e.DataElementType != DataElementType.CellManifestDataElementData);
        var scoped = metadata.Where(e => scope is null || scope.Contains(e.DataElementExtendedGuid)).ToArray();
        var visible = scoped.Where(Visible).ToArray();
        var knowledge = request?.Knowledge is { RequiresFullResponse: false } supported ? supported : null;
        bool Unknown(DataElement e) => ambiguous.Contains(e.SerialNumber) || knowledge?.Contains(e.SerialNumber) != true ||
            mappingSerials.TryGetValue(e.DataElementExtendedGuid, out var serials) &&
            serials.Any(s => knowledge?.Contains(s) != true);
        bool round = request?.RoundKnowledgeToWholeCellChanges == true &&
            visible.Any(Unknown);
        var selected = visible.Where(e => round || Unknown(e))
            .Select(e => e.DataElementExtendedGuid).ToHashSet();
        var advertised = request?.IncludeFilteredOutDataElementsInKnowledge == true ? scoped : visible;
        bool partial = false;
        string? error = null;
        if (request?.MaxDataElements is > 0 and var maximum)
        {
            var candidates = visible.Where(e => selected.Contains(e.DataElementExtendedGuid))
                .OrderBy(e => e.DataElementExtendedGuid.Equals(index))
                .ThenBy(e => e.DataElementExtendedGuid.Guid).ThenBy(e => e.DataElementExtendedGuid.Value).ToArray();
            ulong Size(DataElement element) => checked((ulong)(element.GetSerializedLength() +
                (element.Data is null ? payloadLengths?.GetValueOrDefault(element.DataElementExtendedGuid) ?? 0 : 0)));
            ulong used = 0;
            var page = new HashSet<ExGuid>();
            foreach (var element in candidates)
            {
                // Maximum Data Elements starts partial delivery at a byte
                // threshold. Finish the complete element that crosses it,
                // including when the first element is larger than the value.
                // The independent server response limit still applies.
                if (used >= maximum) break;
                page.Add(element.DataElementExtendedGuid);
                used = checked(used + Size(element));
            }
            partial = page.Count < selected.Count;
            if (partial)
            {
                // Rounding would request the same complete cell on every follow-up.
                // Aliased/null serials cannot identify which individual payloads a
                // client already received. Do not emit a page that cannot progress.
                if (round || request.Knowledge?.RequiresFullResponse == true ||
                    candidates.Any(e => e.SerialNumber.IsNull || ambiguous.Contains(e.SerialNumber)))
                    error = "This query cannot continue within its byte budget using unambiguous element knowledge.";
                else
                {
                    selected = page;
                    // Preserve already held knowledge, and advance only for this
                    // page. Filtered-out elements follow the explicit request flag.
                    advertised = advertised.Where(e => !Visible(e) || !Unknown(e) ||
                        page.Contains(e.DataElementExtendedGuid)).ToArray();
                }
            }
        }
        // A reduced filter scope does not establish complete waterline possession.
        ulong waterline = !partial && scope is null && advertised.Length == metadata.Length ? sequence : 0;
        cellStorageId ??= cell.ShortId;
        return new(selected, new QueryChangesSubResponseData
        {
            // Publish the root mapping after the referenced elements. Partial
            // pages cache immutable elements without announcing a complete root.
            StorageIndexExtendedGuid = partial ? ExGuid.Null : index,
            CellKnowledgeCellGuid = cell.LongId.Guid,
            PartialResult = partial,
            CellKnowledgeTo = partial ? 0 : sequence,
            WaterlineCellStorageExtendedGuid = cellStorageId,
            Waterline = waterline,
            KnowledgeBytes = BinaryKnowledgeBuilder.FromElements(advertised, cellStorageId, waterline,
                mappingSerials: advertised.SelectMany(e => mappingSerials.GetValueOrDefault(e.DataElementExtendedGuid) ?? [])),
        }, error);
    }

    public static FsshttpbResponse Build(ulong requestId, FileQuerySelection selection,
        IEnumerable<DataElement> payloads, QueryChangesSubRequestData? request)
    {
        if (selection.Error is { } error) return Unsupported(requestId, error);
        var package = new DataElementPackage();
        package.DataElements.AddRange(payloads);
        var response = new FsshttpbResponse
        {
            DataElementPackage = package,
            SubResponses = { new FsshttpbSubResponse { RequestId = requestId,
                RequestType = RequestTypes.QueryChanges, Data = selection.Data } },
        };
        QueryChangesResponseShaper.Apply(response, request);
        return response;
    }

    public static FsshttpbResponse Unsupported(ulong id, string message = "This query scope, filter or version is unsupported.") => new()
    {
        SubResponses = { new FsshttpbSubResponse { RequestId = id, RequestType = RequestTypes.QueryChanges,
            Status = true, Error = new ResponseError(ErrorType.Cell,
                (ulong)CellErrorCode.RequestNotSupported, message) } },
    };
}
