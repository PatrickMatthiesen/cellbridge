using CellBridge.FssHttpB;

namespace CellBridge.AspNetCore;

internal sealed record FileQuerySelection(HashSet<ExGuid> PayloadIds, QueryChangesSubResponseData Data);

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
        IReadOnlySet<ExGuid>? scope = null)
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
        // A reduced filter scope does not establish complete waterline possession.
        ulong waterline = scope is null && advertised.Length == metadata.Length ? sequence : 0;
        return new(selected, new QueryChangesSubResponseData
        {
            StorageIndexExtendedGuid = index,
            CellKnowledgeCellGuid = cell.LongId.Guid,
            CellKnowledgeTo = sequence,
            WaterlineCellStorageExtendedGuid = cell.ShortId,
            Waterline = waterline,
            KnowledgeBytes = BinaryKnowledgeBuilder.FromElements(advertised, cell.ShortId, waterline,
                mappingSerials: advertised.SelectMany(e => mappingSerials.GetValueOrDefault(e.DataElementExtendedGuid) ?? [])),
        });
    }

    public static FsshttpbResponse Build(ulong requestId, FileQuerySelection selection,
        IEnumerable<DataElement> payloads, QueryChangesSubRequestData? request)
    {
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

    public static FsshttpbResponse Unsupported(ulong id) => new()
    {
        SubResponses = { new FsshttpbSubResponse { RequestId = id, RequestType = RequestTypes.QueryChanges,
            Status = true, Error = new ResponseError(ErrorType.Protocol,
                (ulong)ProtocolErrorCode.RequestNotSupported, "This query scope, filter or version is unsupported.") } },
    };
}
