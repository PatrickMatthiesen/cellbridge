using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace CellBridge.FssHttpB;

/// <summary>Reads the editor records carried by an editors-table partition stream.</summary>
public static class EditorsTableStreamParser
{
    /// <summary>Parses the eight-byte stream header and the following DEFLATE-compressed XML.</summary>
    public static IReadOnlyList<EditorsTableEditor> Parse(ReadOnlyMemory<byte> stream)
    {
        var header = EditorsTablePartitionBuilder.StreamHeader;
        if (stream.Length <= header.Length || !stream.Span.StartsWith(header))
            throw new InvalidDataException("Invalid editors-table stream header or missing compressed XML.");

        var compressed = stream[header.Length..];
        using var source = MemoryMarshal.TryGetArray(compressed, out var segment) && segment.Array is { } array
            ? new MemoryStream(array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(compressed.ToArray(), writable: false);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        using var reader = XmlReader.Create(deflate, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024,
        });
        var table = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
        if (table?.Name != "EditorsTable")
            throw new InvalidDataException("The editors-table stream must contain an EditorsTable root.");

        var editors = new List<EditorsTableEditor>();
        foreach (var editor in table.Elements("Editor"))
        {
            string? cacheId = ReadField(editor, "CacheID");
            if (string.IsNullOrWhiteSpace(cacheId))
                throw new InvalidDataException("Editor.CacheID is required.");

            long timeout = XmlConvert.ToInt64(ReadField(editor, "Timeout")
                ?? throw new InvalidDataException("Editor.Timeout is required."));
            if (timeout <= 0)
                throw new InvalidDataException("Editor.Timeout must be a positive tick count.");

            string? permission = ReadField(editor, "HasEditorPermission");
            Dictionary<string, byte[]>? metadata = null;
            if (SingleField(editor, "Metadata") is { } metadataElement)
            {
                metadata = new(StringComparer.Ordinal);
                foreach (var entry in metadataElement.Elements())
                {
                    if (entry.Name.Namespace != XNamespace.None || entry.HasElements)
                        throw new InvalidDataException("Editor metadata must contain unqualified base64 elements.");
                    if (!metadata.TryAdd(entry.Name.LocalName, Convert.FromBase64String(entry.Value)))
                        throw new InvalidDataException($"Duplicate editor metadata key '{entry.Name.LocalName}'.");
                }
            }

            editors.Add(new EditorsTableEditor(cacheId, timeout,
                ReadField(editor, "FriendlyName"), ReadField(editor, "LoginName"),
                ReadField(editor, "SIPAddress"), ReadField(editor, "EmailAddress"),
                permission is null ? null : XmlConvert.ToBoolean(permission), metadata));
        }
        return editors;
    }

    private static string? ReadField(XElement editor, string name)
    {
        var field = SingleField(editor, name);
        if (field?.HasElements == true)
            throw new InvalidDataException($"Editor.{name} must contain text.");
        return field?.Value;
    }

    private static XElement? SingleField(XElement editor, string name)
    {
        using var fields = editor.Elements(name).GetEnumerator();
        if (!fields.MoveNext())
            return null;
        var field = fields.Current;
        if (fields.MoveNext())
            throw new InvalidDataException($"Duplicate Editor.{name} element.");
        return field;
    }
}
