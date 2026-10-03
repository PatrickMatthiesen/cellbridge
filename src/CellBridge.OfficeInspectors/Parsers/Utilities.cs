using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CellBridge.OfficeInspectors.Parsers;

internal static class Utilities
{
    private const int MaxEditorsTableBytes = 16 * 1024 * 1024;
    internal static readonly byte[] EditorsTableHeader = [0x1a, 0x5a, 0x3a, 0x30, 0, 0, 0, 0];
    internal static readonly byte[] LocalFileHeader = [0x50, 0x4b, 0x03, 0x04];
    internal static readonly byte[] CentralDirectoryHeader = [0x50, 0x4b, 0x01, 0x02];
    internal static readonly byte[] SignatureCentralDirectory = [0x50, 0x4b, 0x05, 0x05];
    internal static readonly byte[] ArchiveExtralDataRecord = [0x50, 0x4b, 0x06, 0x08];
    internal static readonly byte[] ZIP64EndOfCentralDirectoryRecord = [0x50, 0x4b, 0x06, 0x06];
    internal static readonly byte[] ZIP64EndOfCentralDirectoryLocator = [0x50, 0x4b, 0x06, 0x07];
    internal static readonly byte[] EndOfCentralDirectoryLocator = [0x50, 0x4b, 0x05, 0x06];
    internal static readonly byte[] SignatureDataDescriptor = [0x50, 0x4b, 0x07, 0x08];
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    internal static bool IsEditorsTableHeader(byte[] bytes) => bytes.AsSpan().SequenceEqual(EditorsTableHeader);
    internal static bool IsPNGHeader(byte[] bytes) => bytes.AsSpan().StartsWith(PngHeader);
    internal static bool IsZIPFileHeaderMatch(byte[] bytes, byte[] header) => bytes.AsSpan().StartsWith(header);

    internal static EditorsTable ReadEditorsTable(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = deflate.Read(buffer)) > 0)
        {
            if (output.Length + count > MaxEditorsTableBytes)
                throw new InvalidDataException("Decoded editors table exceeds 16 MiB.");
            output.Write(buffer, 0, count);
        }
        output.Position = 0;
        using var reader = XmlReader.Create(output, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxEditorsTableBytes,
        });
        var document = XDocument.Load(reader);
        return new EditorsTable { Editors = document.Descendants("Editor").Select(ReadEditor).ToArray() };
    }

    private static Editor ReadEditor(XElement node) => new()
    {
        Timeout = (long?)node.Element("Timeout") ?? 0,
        CacheID = (string?)node.Element("CacheID") ?? "",
        FriendlyName = (string?)node.Element("FriendlyName") ?? "",
        LoginName = (string?)node.Element("LoginName") ?? "",
        SIPAddress = (string?)node.Element("SIPAddress") ?? "",
        EmailAddress = (string?)node.Element("EmailAddress") ?? "",
        HasEditorPermission = (bool?)node.Element("HasEditorPermission") ?? false,
        Metadata = node.Element("Metadata")?.Elements().ToDictionary(
            item => item.Name.LocalName,
            item => Encoding.UTF8.GetString(Convert.FromBase64String(item.Value))) ?? [],
    };
}
