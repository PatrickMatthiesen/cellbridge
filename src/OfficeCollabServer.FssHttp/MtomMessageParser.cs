using System.Net.Http.Headers;
using System.Text;

namespace OfficeCollabServer.FssHttp;

/// <summary>A MIME part from an MTOM/XOP message.</summary>
public sealed class MtomPart
{
    public string ContentType { get; init; } = string.Empty;
    public string? ContentId { get; init; }
    public byte[] Content { get; init; } = Array.Empty<byte>();
}

/// <summary>Extracts SOAP and binary parts from an MTOM/XOP message.</summary>
public static class MtomMessageParser
{
    public static IReadOnlyList<MtomPart> Parse(ReadOnlyMemory<byte> message, string contentType)
    {
        var mediaType = MediaTypeHeaderValue.Parse(contentType);
        var boundary = mediaType.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value
            ?.Trim('"');

        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidDataException("MTOM Content-Type is missing a boundary.");
        }

        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var bytes = message.ToArray();
        var parts = new List<MtomPart>();
        var position = 0;

        while (true)
        {
            var delimiterPosition = FindBoundary(bytes, delimiter, position);
            if (delimiterPosition < 0)
            {
                break;
            }

            var contentStart = delimiterPosition + delimiter.Length;
            if (contentStart + 1 < bytes.Length && bytes[contentStart] == '-' && bytes[contentStart + 1] == '-')
            {
                break;
            }

            while (contentStart < bytes.Length && bytes[contentStart] is (byte)' ' or (byte)'\t')
                contentStart++;

            if (contentStart + 1 < bytes.Length && bytes[contentStart] == '\r' && bytes[contentStart + 1] == '\n')
            {
                contentStart += 2;
            }

            var nextDelimiter = FindBoundary(bytes, delimiter, contentStart);
            if (nextDelimiter < 0)
            {
                throw new InvalidDataException("MTOM part is missing its terminating boundary.");
            }

            var partEnd = nextDelimiter;
            if (partEnd >= 2 && bytes[partEnd - 2] == '\r' && bytes[partEnd - 1] == '\n')
            {
                partEnd -= 2;
            }

            var separator = IndexOf(bytes, new byte[] { 13, 10, 13, 10 }, contentStart, partEnd);
            if (separator < 0)
            {
                throw new InvalidDataException("MTOM part is missing its header separator.");
            }

            var headers = Encoding.ASCII.GetString(bytes, contentStart, separator - contentStart);
            var bodyStart = separator + 4;
            var headersByName = ParseHeaders(headers);

            parts.Add(new MtomPart
            {
                ContentType = headersByName.GetValueOrDefault("content-type") ?? string.Empty,
                ContentId = headersByName.GetValueOrDefault("content-id")?.Trim('<', '>'),
                Content = bytes[bodyStart..partEnd],
            });

            position = nextDelimiter;
        }

        if (parts.Count == 0)
        {
            throw new InvalidDataException("MTOM message contains no MIME parts.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            if (part.ContentId is { Length: > 0 } id && !ids.Add(id))
                throw new InvalidDataException("MTOM message contains duplicate Content-ID values.");
        }

        return parts;
    }

    private static int FindBoundary(byte[] source, byte[] delimiter, int start)
    {
        for (var i = start; i <= source.Length - delimiter.Length; i++)
        {
            if (i != 0 && (i < 2 || source[i - 2] != '\r' || source[i - 1] != '\n'))
                continue;
            if (!source.AsSpan(i, delimiter.Length).SequenceEqual(delimiter))
                continue;
            var after = i + delimiter.Length;
            if (after + 1 < source.Length && source[after] == '-' && source[after + 1] == '-')
                after += 2;
            // MIME permits transport padding after a delimiter.
            while (after < source.Length && source[after] is (byte)' ' or (byte)'\t')
                after++;
            if (after == source.Length ||
                (after + 1 < source.Length && source[after] == '\r' && source[after + 1] == '\n'))
                return i;
        }
        return -1;
    }

    private static Dictionary<string, string> ParseHeaders(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return result;
    }

    private static int IndexOf(byte[] source, byte[] value, int start, int? end = null)
    {
        var limit = end ?? source.Length;
        for (var i = start; i <= limit - value.Length; i++)
        {
            if (source.AsSpan(i, value.Length).SequenceEqual(value))
            {
                return i;
            }
        }

        return -1;
    }
}
