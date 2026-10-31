using CellBridge.FssHttpB;

namespace CellBridge.AspNetCore;

/// <summary>Combines independently shaped queries without changing their knowledge or publication order.</summary>
internal sealed class QueryChangesResponseAssembler(FsshttpbResponse response, long maxBytes,
    RequestHashOptions? hashOptions = null, ProtocolHashingOptions? hashing = null)
{
    private readonly Dictionary<ExGuid, DataElement> _elements = new();
    private readonly HashSet<ExGuid> _saveIds = [];
    private readonly List<QueryBudget> _queryBudgets = [];
    private readonly Dictionary<ExGuid, DataElement> _projections = new();
    private readonly ProtocolHashingOptions _hashing = hashing ?? ProtocolHashingOptions.Default;

    private sealed record QueryBudget(HashSet<ExGuid> Ids, ulong Threshold, ulong Maximum);

    // This budget counts serialized data elements, as page selection does.
    // Mandatory save payloads must pass this same check before publication.
    public bool CanAppend(DataElementPackage? package) => TryCombine(package, true, null, out _, out _, out _, out _);

    public void AppendSave(FsshttpbResponse save)
    {
        if (!TryCombine(save.DataElementPackage, true, null, out var proposed, out var projected, out var saveIds, out var error))
            throw new InvalidOperationException($"A published save response failed its preflight: {error}");
        CommitPackage(save.DataElementPackage, proposed, projected, saveIds);
        response.SubResponses.AddRange(save.SubResponses);
    }

    public void Append(FsshttpbResponse query, QueryChangesSubRequestData? controls = null)
    {
        // Selection can finish an element beyond the client threshold. Keep
        // that admitted page's allowance when checking later index merges or
        // hashing projections, without relaxing the aggregate server limit.
        var budget = query.DataElementPackage is { } package && controls?.MaxDataElements is > 0
            ? new QueryBudget(package.DataElements.Select(e => e.DataElementExtendedGuid).ToHashSet(),
                controls.MaxDataElements.Value,
                Math.Max(controls.MaxDataElements.Value, checked((ulong)package.DataElements.Sum(Size))))
            : null;
        if (!TryCombine(query.DataElementPackage, false, budget, out var proposed, out var projected, out var saveIds, out var error))
        {
            Reject(query, error!);
            response.SubResponses.AddRange(query.SubResponses);
            return;
        }
        CommitPackage(query.DataElementPackage, proposed, projected, saveIds);
        if (budget is not null)
            _queryBudgets.Add(budget with { Maximum = Math.Max(budget.Threshold,
                checked((ulong)budget.Ids.Sum(id => Size(projected[id])))) });
        response.SubResponses.AddRange(query.SubResponses);
    }

    private bool TryCombine(DataElementPackage? package, bool save, QueryBudget? budget,
        out Dictionary<ExGuid, DataElement> proposed, out Dictionary<ExGuid, DataElement> projected,
        out HashSet<ExGuid> saveIds, out string? error)
    {
        proposed = new Dictionary<ExGuid, DataElement>(_elements);
        projected = new();
        saveIds = new(_saveIds);
        error = null;
        foreach (var element in package?.DataElements ?? [])
        {
            if (save) saveIds.Add(element.DataElementExtendedGuid);
            if (proposed.TryGetValue(element.DataElementExtendedGuid, out var previous))
            {
                // A storage index is a transmission envelope. A PutChanges delta
                // and a later full query can describe the same accepted map.
                if (previous.DataElementType == DataElementType.StorageIndexDataElementData &&
                    element.DataElementType == previous.DataElementType)
                {
                    try { proposed[element.DataElementExtendedGuid] = CombineIndexes(previous, element); }
                    catch (InvalidDataException ex) { error = ex.Message; return false; }
                    continue;
                }
                if (previous.DataElementType != element.DataElementType ||
                    !(previous.Data ?? []).SequenceEqual(element.Data ?? []) ||
                    !previous.SerialNumber.IsNull && !element.SerialNumber.IsNull && !previous.SerialNumber.Equals(element.SerialNumber))
                {
                    error = "Response results contain conflicting immutable data-element identities.";
                    return false;
                }
                if (previous.SerialNumber.IsNull && !element.SerialNumber.IsNull)
                {
                    proposed[element.DataElementExtendedGuid] = element;
                }
            }
            else
            {
                proposed.Add(element.DataElementExtendedGuid, element);
            }
        }
        long proposedBytes = 0;
        try
        {
            foreach (var (id, element) in proposed)
            {
                DataElement output;
                if (saveIds.Contains(id)) output = element;
                else if (_elements.TryGetValue(id, out var original) && ReferenceEquals(original, element) &&
                    _projections.TryGetValue(id, out var cached)) output = cached;
                else output = ObjectGroupWireHash.Project(element, hashOptions, _hashing);
                projected.Add(id, output);
                proposedBytes = checked(proposedBytes + Size(output));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            error = "The query contains an object group unsuitable for negotiated hashing.";
            return false;
        }
        if (proposedBytes > maxBytes)
        {
            error = "The combined response payload exceeds the server data-element budget.";
            return false;
        }
        var candidate = projected;
        foreach (var constraint in budget is null ? _queryBudgets : _queryBudgets.Append(budget))
        {
            ulong used = 0;
            foreach (var id in constraint.Ids)
            {
                ulong size = (ulong)Size(candidate[id]);
                if (size > constraint.Maximum - used)
                {
                    error = "The projected response exceeds an admitted QueryChanges page allowance.";
                    return false;
                }
                used += size;
            }
        }
        return true;
    }

    private static DataElement CombineIndexes(DataElement previous, DataElement next)
    {
        if (!previous.SerialNumber.IsNull && !next.SerialNumber.IsNull && !previous.SerialNumber.Equals(next.SerialNumber))
            throw new InvalidDataException("Response results contain conflicting storage-index serials.");
        var mappings = StorageIndexPatch.Read(previous).ToDictionary(mapping => (mapping.Type, mapping.Revision, mapping.Cell));
        foreach (var mapping in StorageIndexPatch.Read(next))
        {
            var key = (mapping.Type, mapping.Revision, mapping.Cell);
            if (mappings.TryGetValue(key, out var existing) &&
                (!existing.Target.Equals(mapping.Target) || !existing.Serial.Equals(mapping.Serial)))
                throw new InvalidDataException("Response results contain conflicting storage-index mapping values.");
            mappings[key] = mapping;
        }
        return new(previous.DataElementType, previous.DataElementExtendedGuid,
            previous.SerialNumber.IsNull ? next.SerialNumber : previous.SerialNumber)
        { Data = StorageIndexPatch.Serialize(mappings.Values) };
    }

    private void CommitPackage(DataElementPackage? package, Dictionary<ExGuid, DataElement> proposed,
        Dictionary<ExGuid, DataElement> projected, HashSet<ExGuid> saveIds)
    {
        if (package is not null)
        {
            response.DataElementPackage ??= new();
            response.DataElementPackage.DataElements.Clear();
            response.DataElementPackage.DataElements.AddRange(projected.Values);
            _elements.Clear();
            foreach (var pair in proposed) _elements.Add(pair.Key, pair.Value);
            _projections.Clear();
            foreach (var pair in projected) _projections.Add(pair.Key, pair.Value);
            _saveIds.Clear();
            _saveIds.UnionWith(saveIds);
        }
    }

    // Admission has always counted Current framing, independently of the final
    // response profile. Measure the same bytes without copying every payload.
    private static long Size(DataElement element) => element.GetSerializedLength();

    private static void Reject(FsshttpbResponse query, string message)
    {
        query.DataElementPackage = null;
        foreach (var subResponse in query.SubResponses.Where(s => s.RequestType == RequestTypes.QueryChanges && !s.Status))
        {
            subResponse.Status = true;
            subResponse.Data = null;
            subResponse.Error = new ResponseError(ErrorType.Cell,
                (ulong)CellErrorCode.RequestNotSupported, message);
        }
    }
}
