using System.Text;

namespace CellBridge.FssHttp;

/// <summary>A prepared MTOM message. Referenced binary buffers must remain unchanged during use.</summary>
public sealed class MtomResponseMessage
{
    private readonly ReadOnlyMemory<byte>[] _segments;
    public string ContentType { get; }
    public long ContentLength { get; }

    internal MtomResponseMessage(string contentType, IEnumerable<ReadOnlyMemory<byte>> segments)
    {
        ContentType = contentType;
        _segments = segments.ToArray();
        foreach (var segment in _segments)
            ContentLength = checked(ContentLength + segment.Length);
    }

    internal static string ContentId(int index) => $"http://tempuri.org/{index}";

    internal static MtomResponseMessage Create(string soapXml, string soapNamespace,
        IReadOnlyList<ReadOnlyMemory<byte>> binaryParts)
    {
        var boundary = "urn:uuid:" + Guid.NewGuid().ToString().ToUpperInvariant();
        var rootContentId = ContentId(0);
        var soapMediaType = soapNamespace == CellStorageRequest.Soap12Namespace ? "application/soap+xml" : "text/xml";
        var segments = new List<ReadOnlyMemory<byte>>();

        // Keep the WCF/SharePoint preamble and root headers accepted by Word.
        AddAscii($"\r\n--{boundary}\r\n");
        AddAscii($"Content-ID: <{rootContentId}>\r\nContent-Transfer-Encoding: 8bit\r\nContent-Type: application/xop+xml;charset=utf-8;type=\"{soapMediaType}\"\r\n\r\n");
        segments.Add(Encoding.UTF8.GetBytes(soapXml));
        AddAscii("\r\n");

        for (int i = 0; i < binaryParts.Count; i++)
        {
            AddAscii($"--{boundary}\r\nContent-ID: <{ContentId(i + 1)}>\r\nContent-Transfer-Encoding: binary\r\nContent-Type: application/octet-stream\r\n\r\n");
            segments.Add(binaryParts[i]);
            AddAscii("\r\n");
        }

        AddAscii($"--{boundary}--\r\n");
        return new MtomResponseMessage(
            $"multipart/related; type=\"application/xop+xml\"; boundary=\"{boundary}\"; start=\"<{rootContentId}>\"; start-info=\"{soapMediaType}\"",
            segments);

        void AddAscii(string value) => segments.Add(Encoding.ASCII.GetBytes(value));
    }

    /// <summary>Returns an independent copy of the complete message.</summary>
    public byte[] ToArray()
    {
        if (ContentLength > Array.MaxLength)
            throw new InvalidOperationException("The MTOM message exceeds the byte-array limit; use streaming output.");
        var bytes = GC.AllocateUninitializedArray<byte>((int)ContentLength);
        int position = 0;
        foreach (var segment in _segments)
        {
            segment.Span.CopyTo(bytes.AsSpan(position));
            position += segment.Length;
        }
        return bytes;
    }

    /// <summary>Writes at the destination's current position and leaves it open.</summary>
    public async ValueTask WriteToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination must be writable.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var segment in _segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(segment, cancellationToken);
        }
    }
}
