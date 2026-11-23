using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace CellBridge.Client.Excel;

/// <summary>Rebuilds the ZIP directory and copies untouched local records verbatim.</summary>
internal sealed class RawExcelZip : IDisposable
{
    private const uint EndSignature = 0x06054b50;
    private const uint DirectorySignature = 0x02014b50;
    private const uint LocalSignature = 0x04034b50;
    private readonly byte[] _bytes;
    private readonly ZipArchive _archive;
    private readonly int _end;
    private readonly int _directory;
    private readonly Entry[] _entries;
    private readonly Dictionary<string, ZipArchiveEntry> _parts;

    private sealed record Entry(string Name, int CentralOffset, int CentralLength, int LocalOffset, int CompressedSize);

    internal RawExcelZip(byte[] bytes, long maxExpandedBytes, ExcelMemoryBudget budget)
    {
        _bytes = bytes;
        _end = FindEnd(bytes);
        if (_end < 0)
            throw new InvalidDataException("The workbook ZIP directory is missing.");
        var directory = U32(bytes, _end + 16);
        var size = U32(bytes, _end + 12);
        var count = U16(bytes, _end + 10);
        if (U32(bytes, _end + 4) != 0 || count != U16(bytes, _end + 8) ||
            count == ushort.MaxValue || (long)directory + size != _end)
            throw new InvalidDataException("Excel requires a single-disk ZIP32 package.");
        _directory = (int)directory;
        budget.Charge(count * 640L + size * 2L + 65_536);

        _archive = new ZipArchive(new MemoryStream(bytes, writable: false));
        try
        {
            if (_archive.Entries.Count != count)
                throw new InvalidDataException("The ZIP directory entry count is invalid.");
            _entries = new Entry[count];
            _parts = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            var centralOffset = _directory;
            long expanded = 0;
            var buffer = new byte[65536];
            for (var i = 0; i < count; i++)
            {
                if (centralOffset > _end - 46 || U32(bytes, centralOffset) != DirectorySignature ||
                    U16(bytes, centralOffset + 34) != 0 || (U16(bytes, centralOffset + 8) & 1) != 0)
                    throw new InvalidDataException("The ZIP directory contains an unsupported entry.");
                var nameSize = U16(bytes, centralOffset + 28);
                var centralLength = 46 + nameSize + U16(bytes, centralOffset + 30) + U16(bytes, centralOffset + 32);
                var localOffset = U32(bytes, centralOffset + 42);
                var compressedSize = U32(bytes, centralOffset + 20);
                if (centralLength > _end - centralOffset || localOffset > int.MaxValue || compressedSize > int.MaxValue)
                    throw new InvalidDataException("The ZIP entry extends past the directory or uses ZIP64.");
                var part = _archive.Entries[i];
                if (!_parts.TryAdd(part.FullName, part))
                    throw new InvalidDataException("The workbook contains duplicate part names.");
                _entries[i] = new Entry(part.FullName, centralOffset, centralLength, (int)localOffset, (int)compressedSize);
                centralOffset += centralLength;
                if (part.Length > maxExpandedBytes - expanded)
                    throw new InvalidDataException("The workbook exceeds the expanded package limit.");
                using var stream = part.Open();
                long partLength = 0;
                int read;
                while ((read = stream.Read(buffer)) != 0)
                {
                    expanded += read;
                    partLength += read;
                    if (expanded > maxExpandedBytes || partLength > part.Length)
                        throw new InvalidDataException("The workbook exceeds its declared or allowed expanded size.");
                }
                if (partLength != part.Length)
                    throw new InvalidDataException("The ZIP entry expanded size does not match its header.");
            }
            if (centralOffset != _end)
                throw new InvalidDataException("The ZIP directory length is invalid.");
            ValidateLocalRecords();
            ExpandedBytes = expanded;
        }
        catch
        {
            _archive.Dispose();
            throw;
        }
    }

    internal int PackageBytes => _bytes.Length;
    internal long ExpandedBytes { get; }
    internal bool Contains(string part) => _parts.ContainsKey(part);
    internal long PartLength(string part) => GetPart(part).Length;

    internal string? ReadRootAttribute(string part, XName name)
    {
        var entry = GetPart(part);
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = entry.Length,
        });
        if (reader.MoveToContent() != XmlNodeType.Element)
            throw new InvalidDataException($"'{part}' has no XML root element.");
        return reader.GetAttribute(name.LocalName, name.NamespaceName);
    }

    internal XDocument ReadXml(string part, ExcelMemoryBudget budget)
    {
        var entry = GetPart(part);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = entry.Length,
        };
        budget.Charge(entry.Length * 4);
        using (var preflight = entry.Open())
        using (var probe = XmlReader.Create(preflight, settings))
        {
            while (probe.Read())
            {
                if (probe.Depth > 128)
                    throw new InvalidDataException("The workbook XML exceeds the nesting limit.");
                budget.Charge(96L + probe.AttributeCount * 96L);
            }
        }
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, settings);
        // LINQ to XML keeps the original markup, including compatibility and revision attributes.
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private ZipArchiveEntry GetPart(string part) => _parts.TryGetValue(part, out var entry)
        ? entry : throw new InvalidDataException($"The workbook is missing '{part}'.");

    internal byte[] Rewrite(IReadOnlyDictionary<string, XDocument> changes, IReadOnlySet<string> removed,
        ExcelWorkbookLimits limits, ExcelMemoryBudget budget)
    {
        if (changes.Count == 0 && removed.Count == 0)
            return _bytes.ToArray();

        using var replacements = new MemoryStream();
        using (var zip = new ZipArchive(replacements, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in changes)
            {
                var replacement = zip.CreateEntry(name, CompressionLevel.Optimal);
                replacement.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var target = replacement.Open();
                xml.Save(target, SaveOptions.DisableFormatting);
            }
        }
        using var changedZip = new RawExcelZip(replacements.ToArray(), limits.MaxExpandedBytes, budget);
        var changedNames = changedZip._entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        var added = changedZip._entries.Where(entry => !_parts.ContainsKey(entry.Name)).ToArray();
        long expanded = ExpandedBytes + changedZip.ExpandedBytes;
        foreach (var name in changes.Keys)
            if (Contains(name))
                expanded -= PartLength(name);
        foreach (var name in removed)
            expanded -= PartLength(name);
        var entryCount = _entries.Length - removed.Count + added.Length;
        if (expanded > limits.MaxExpandedBytes || entryCount >= ushort.MaxValue)
            throw new InvalidDataException("The edited workbook exceeds the expanded package or ZIP32 entry limit.");

        using var output = new MemoryStream();
        var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var original = _entries.OrderBy(entry => entry.LocalOffset).ToArray();
        output.Write(_bytes.AsSpan(0, original.Length == 0 ? _directory : original[0].LocalOffset));
        for (var i = 0; i < original.Length; i++)
        {
            var entry = original[i];
            if (removed.Contains(entry.Name))
                continue;
            offsets.Add(entry.Name, checked((int)output.Position));
            if (changedNames.TryGetValue(entry.Name, out var replacement))
                changedZip.WriteLocal(output, replacement);
            else
            {
                int next = i + 1 < original.Length ? original[i + 1].LocalOffset : _directory;
                output.Write(_bytes.AsSpan(entry.LocalOffset, next - entry.LocalOffset));
            }
        }
        foreach (var entry in added)
        {
            offsets.Add(entry.Name, checked((int)output.Position));
            changedZip.WriteLocal(output, entry);
        }
        var directoryOffset = checked((int)output.Position);
        foreach (var entry in _entries)
        {
            if (removed.Contains(entry.Name))
                continue;
            if (changedNames.TryGetValue(entry.Name, out var replacement))
                changedZip.WriteCentral(output, replacement, offsets[entry.Name]);
            else
                WriteCentral(output, entry, offsets[entry.Name]);
        }
        foreach (var entry in added)
            changedZip.WriteCentral(output, entry, offsets[entry.Name]);

        var end = _bytes.AsSpan(_end).ToArray();
        var count = checked((ushort)entryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), count);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), count);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), checked((uint)(output.Position - directoryOffset)));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)directoryOffset);
        if (output.Position + end.Length > limits.MaxPackageBytes)
            throw new InvalidDataException("The edited workbook exceeds the package size limit.");
        output.Write(end);
        return output.ToArray();
    }

    private void WriteLocal(Stream output, Entry entry)
    {
        var i = Array.IndexOf(_entries, entry);
        var next = i + 1 == _entries.Length ? _directory : _entries[i + 1].LocalOffset;
        output.Write(_bytes.AsSpan(entry.LocalOffset, next - entry.LocalOffset));
    }

    private void WriteCentral(Stream output, Entry entry, int localOffset)
    {
        output.Write(_bytes.AsSpan(entry.CentralOffset, 42));
        Span<byte> offset = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(offset, localOffset);
        output.Write(offset);
        output.Write(_bytes.AsSpan(entry.CentralOffset + 46, entry.CentralLength - 46));
    }

    private void ValidateLocalRecords()
    {
        var sorted = _entries.OrderBy(entry => entry.LocalOffset).ToArray();
        for (var i = 0; i < sorted.Length; i++)
        {
            var entry = sorted[i];
            var next = i + 1 == sorted.Length ? _directory : sorted[i + 1].LocalOffset;
            var start = entry.LocalOffset;
            if (start > next - 30 || U32(_bytes, start) != LocalSignature)
                throw new InvalidDataException("The ZIP local record is invalid.");
            var nameSize = U16(_bytes, start + 26);
            if ((long)start + 30 + nameSize + U16(_bytes, start + 28) + entry.CompressedSize > next ||
                nameSize != U16(_bytes, entry.CentralOffset + 28) ||
                !_bytes.AsSpan(start + 30, nameSize).SequenceEqual(_bytes.AsSpan(entry.CentralOffset + 46, nameSize)))
                throw new InvalidDataException("ZIP local records overlap or disagree with the directory.");
        }
    }

    // Read local record boundaries without opening or decompressing the package.
    // Unsupported ZIP layouts can still use the default file chunking.
    internal static int[] LargeRecordBoundaries(ReadOnlySpan<byte> bytes, int minimumSize)
    {
        int end = FindEnd(bytes);
        if (end < 0 || U32(bytes, end + 4) != 0)
            return [];
        uint directory = U32(bytes, end + 16);
        uint directorySize = U32(bytes, end + 12);
        int count = U16(bytes, end + 10);
        if (count == ushort.MaxValue || count != U16(bytes, end + 8) ||
            (long)directory + directorySize != end || count > directorySize / 46)
            return [];

        var offsets = new int[count + 1];
        int central = (int)directory;
        for (int i = 0; i < count; i++)
        {
            if (central > end - 46 || U32(bytes, central) != DirectorySignature)
                return [];
            uint local = U32(bytes, central + 42);
            if ((long)local + 30 > directory || U32(bytes, (int)local) != LocalSignature)
                return [];
            offsets[i] = (int)local;
            int length = 46 + U16(bytes, central + 28) + U16(bytes, central + 30) + U16(bytes, central + 32);
            if (length > end - central)
                return [];
            central += length;
        }
        if (central != end)
            return [];
        offsets[count] = (int)directory;
        Array.Sort(offsets);
        var boundaries = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int start = offsets[i];
            int next = offsets[i + 1];
            if (next - start < 30)
                return [];
            if (next - start < minimumSize)
                continue;
            if (start != 0 && (boundaries.Count == 0 || boundaries[^1] != start))
                boundaries.Add(start);
            boundaries.Add(next);
        }
        return boundaries.ToArray();
    }

    private static int FindEnd(ReadOnlySpan<byte> bytes)
    {
        for (int i = bytes.Length - 22, min = Math.Max(0, bytes.Length - 22 - ushort.MaxValue); i >= min; i--)
            if (U32(bytes, i) == EndSignature && U16(bytes, i + 20) == bytes.Length - i - 22)
                return i;
        return -1;
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    public void Dispose() => _archive.Dispose();
}
