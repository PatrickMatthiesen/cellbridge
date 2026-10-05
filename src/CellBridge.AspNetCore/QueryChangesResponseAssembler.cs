using CellBridge.FssHttpB;

namespace CellBridge.AspNetCore;

/// <summary>Combines independently shaped queries without changing their knowledge or publication order.</summary>
internal sealed class QueryChangesResponseAssembler(FsshttpbResponse response, long maxBytes)
{
    private readonly Dictionary<ExGuid, DataElement> _elements = new();
    private long _bytes;

    public void Append(FsshttpbResponse query)
    {
        var proposed = new Dictionary<ExGuid, DataElement>(_elements);
        long proposedBytes = _bytes;
        foreach (var element in query.DataElementPackage?.DataElements ?? [])
        {
            if (proposed.TryGetValue(element.DataElementExtendedGuid, out var previous))
            {
                if (previous.DataElementType != element.DataElementType ||
                    !(previous.Data ?? []).SequenceEqual(element.Data ?? []) ||
                    !previous.SerialNumber.IsNull && !element.SerialNumber.IsNull && !previous.SerialNumber.Equals(element.SerialNumber))
                {
                    Reject(query, "Repeated query results contain conflicting immutable data-element identities.");
                    response.SubResponses.AddRange(query.SubResponses);
                    return;
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
            Reject(query, "The combined query results exceed the server response budget.");
            response.SubResponses.AddRange(query.SubResponses);
            return;
        }
        if (query.DataElementPackage is not null)
        {
            response.DataElementPackage ??= new();
            response.DataElementPackage.DataElements.Clear();
            response.DataElementPackage.DataElements.AddRange(proposed.Values);
            _elements.Clear();
            foreach (var pair in proposed) _elements.Add(pair.Key, pair.Value);
            _bytes = proposedBytes;
        }
        response.SubResponses.AddRange(query.SubResponses);
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
