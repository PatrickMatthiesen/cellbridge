namespace CellBridge.FssHttp;

/// <summary>A prepared MTOM message. Referenced binary arrays must remain unchanged during use.</summary>
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
