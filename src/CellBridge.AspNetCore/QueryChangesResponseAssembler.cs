using CellBridge.FssHttpB;

namespace CellBridge.AspNetCore;

/// <summary>Combines independently shaped queries without changing their knowledge or publication order.</summary>
internal sealed class QueryChangesResponseAssembler(FsshttpbResponse response, long maxBytes)
{
    private readonly Dictionary<ExGuid, DataElement> _elements = new();
    private long _bytes;

    // This budget counts serialized data elements, as the query shaper does.
    // Mandatory save payloads must pass this same check before publication.
    public bool CanAppend(DataElementPackage? package) => TryCombine(package, out _, out _, out _);

    public void AppendSave(FsshttpbResponse save)
    {
        if (!TryCombine(save.DataElementPackage, out var proposed, out var bytes, out var error))
            throw new InvalidOperationException($"A published save response failed its preflight: {error}");
        CommitPackage(save.DataElementPackage, proposed, bytes);
        response.SubResponses.AddRange(save.SubResponses);
    }

    public void Append(FsshttpbResponse query)
    {
        if (!TryCombine(query.DataElementPackage, out var proposed, out var bytes, out var error))
        {
            Reject(query, error!);
            response.SubResponses.AddRange(query.SubResponses);
            return;
        }
        CommitPackage(query.DataElementPackage, proposed, bytes);
        response.SubResponses.AddRange(query.SubResponses);
    }

    private bool TryCombine(DataElementPackage? package, out Dictionary<ExGuid, DataElement> proposed,
        out long proposedBytes, out string? error)
    {
        proposed = new Dictionary<ExGuid, DataElement>(_elements);
        proposedBytes = _bytes;
        error = null;
        foreach (var element in package?.DataElements ?? [])
        {
            if (proposed.TryGetValue(element.DataElementExtendedGuid, out var previous))
            {
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
                    proposedBytes = checked(proposedBytes + Size(element) - Size(previous));
                }
            }
            else
            {
                proposed.Add(element.DataElementExtendedGuid, element);
                proposedBytes = checked(proposedBytes + Size(element));
            }
        }
        if (proposedBytes > maxBytes)
        {
            error = "The combined response payload exceeds the server data-element budget.";
            return false;
        }
        return true;
    }

    private void CommitPackage(DataElementPackage? package, Dictionary<ExGuid, DataElement> proposed, long proposedBytes)
    {
        if (package is not null)
        {
            response.DataElementPackage ??= new();
            response.DataElementPackage.DataElements.Clear();
            response.DataElementPackage.DataElements.AddRange(proposed.Values);
            _elements.Clear();
            foreach (var pair in proposed) _elements.Add(pair.Key, pair.Value);
            _bytes = proposedBytes;
        }
    }

    private static long Size(DataElement element)
    {
        var writer = new BinaryWriterEx();
        element.Serialize(writer);
        return writer.Length;
    }

    private static void Reject(FsshttpbResponse query, string message)
    {
        query.DataElementPackage = null;
        foreach (var subResponse in query.SubResponses.Where(s => s.RequestType == RequestTypes.QueryChanges && !s.Status))
        {
            subResponse.Status = true;
            subResponse.Data = null;
            subResponse.Error = new ResponseError(ErrorType.Protocol,
                (ulong)ProtocolErrorCode.RequestNotSupported, message);
        }
    }
}
