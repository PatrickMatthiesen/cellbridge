using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CellBridge.FssHttpB;

namespace CellBridge.TestFixtures;

/// <summary>A synthetic DOCX workload with a fixed padding part and real small file edits.</summary>
public sealed class IncrementalOfficeWorkload
{
    public const int DocumentPartBytes = 4096;
    private static readonly DateTimeOffset EntryTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly byte[] padding;
    public int PackageKiB { get; }
    public int ChangedDocumentPartBytes { get; }

    public IncrementalOfficeWorkload(int packageKiB, int seed = 42, int changedPartKiB = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(packageKiB, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(packageKiB, Array.MaxLength / 1024);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(changedPartKiB);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(changedPartKiB, packageKiB);
        PackageKiB = packageKiB;
        ChangedDocumentPartBytes = checked(changedPartKiB * 1024);
        var overhead = BuildPackage([], 0).Bytes.Length;
        var paddingBytes = checked(packageKiB * 1024 - overhead);
        if (paddingBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(changedPartKiB), "The XML part and ZIP metadata must leave room for unchanged padding.");
        padding = new byte[paddingBytes];
        new Random(seed).NextBytes(padding);
    }

    public byte[] CreateContent(int revision = 0) => BuildPackage(padding, revision).Bytes;

    /// <summary>
    /// The first request installs independently reusable chunks. Later requests send only changed chunks,
    /// and reference unchanged groups already present in the client's graph. No provider reads are needed.
    /// </summary>
    public IncrementalOfficeSave CreateSave(PartitionGraphSnapshot currentGraph, int revision)
    {
        ArgumentNullException.ThrowIfNull(currentGraph);
        var layout = BuildPackage(padding, revision);
        var cell = currentGraph.FileCell;
        var space = Guid.NewGuid();
        ExGuid Id(uint value) => new(value, space);
        SerialNumber Serial(ulong value) => new(space, value);
        var package = new DataElementPackage();
        var selectedGroups = new List<ExGuid> { Id(4) };
        var leaves = new List<ExGuid>();
        var existing = currentGraph.ElementMetadata.ToDictionary(element => element.DataElementExtendedGuid);
        var changedBytes = 0;
        var sent = 0;
        var reused = 0;
        var chunks = new List<(int Offset, int Length)>();
        // The fixed ZIP prefix includes the large padding part. The final chunks contain
        // the document entry and ZIP directory, including its changing CRC.
        for (var offset = 0; offset < layout.DocumentOffset; offset += FsshttpdChunker.DefaultChunkSize)
            chunks.Add((offset, Math.Min(FsshttpdChunker.DefaultChunkSize, layout.DocumentOffset - offset)));
        for (var offset = layout.DocumentOffset; offset < layout.Bytes.Length; offset += FsshttpdChunker.DefaultChunkSize)
            chunks.Add((offset, Math.Min(FsshttpdChunker.DefaultChunkSize, layout.Bytes.Length - offset)));
        foreach (var (offset, length) in chunks)
        {
            var content = layout.Bytes.AsSpan(offset, length).ToArray();
            var digest = SHA256.HashData(content);
            var identityDigest = SHA256.HashData([..cell.LongId.Guid.ToByteArray(), ..digest]);
            var chunkSpace = new Guid(identityDigest.AsSpan(0, 16));
            var groupId = new ExGuid(100, chunkSpace);
            var leafId = new ExGuid(101, chunkSpace);
            var bytesId = new ExGuid(102, chunkSpace);
            var group = Group(groupId, new SerialNumber(chunkSpace, 100),
                new(leafId, FsshttpdNodeSerializer.SerializeLegacyLeaf(new((ulong)offset, content, leafId, [])), [bytesId]),
                new(bytesId, content, []));
            selectedGroups.Add(groupId);
            leaves.Add(leafId);
            if (existing.ContainsKey(groupId))
            {
                if (currentGraph.ConflictsWith(group))
                    throw new InvalidDataException("The client's retained chunk identity has different content.");
                reused++;
            }
            else
            {
                package.DataElements.Add(group);
                changedBytes += length;
                sent++;
            }
        }
        var root = new ExGuid(2, StorageManifestBuilder.RootExtendedGuid);
        package.DataElements.AddRange([
            StorageManifestBuilder.BuildStorageManifestDataElement(Id(1), Serial(1), root, cell),
            StorageManifestBuilder.BuildCellManifestDataElement(Id(2), Serial(2), Id(7)),
            StorageManifestBuilder.BuildRevisionManifestDataElement(Id(3), Serial(3), Id(7), ExGuid.Null,
                root, Id(6), selectedGroups),
            Group(Id(4), Serial(4), new StreamObject(Id(6),
                FsshttpdNodeSerializer.SerializeLegacyIntermediate((ulong)layout.Bytes.Length, layout.Bytes), leaves.ToArray())),
            StorageManifestBuilder.BuildStorageIndexDataElement(Id(5), Serial(5), Id(1), Serial(1),
                cell, Id(2), Serial(2), Id(7), Id(3), Serial(3)),
        ]);
        var expected = currentGraph.StorageIndexes.Single(element => element.DataElementExtendedGuid.Equals(currentGraph.StorageIndex));
        package.DataElements.Add(new(expected.DataElementType,
            new(expected.DataElementExtendedGuid.Value, expected.DataElementExtendedGuid.Guid),
            new(expected.SerialNumber.Guid, expected.SerialNumber.Value)) { Data = expected.Data!.ToArray() });
        var request = new FsshttpbCellRequest { DataElementPackage = package };
        request.SubRequests.Add(new(RequestTypes.PutChanges)
        {
            RequestId = checked((ulong)revision + 1),
            Data = new PutChangesSubRequestData
            {
                StorageIndex = Id(5), ExpectedStorageIndex = currentGraph.StorageIndex,
                HasAdditionalFlags = true, AdditionalFlagsBits = 1,
            },
        });
        return new(request, layout.Bytes, request.ToByteArray(), changedBytes, sent, reused);
    }

    private (byte[] Bytes, int DocumentOffset) BuildPackage(byte[] padding, int revision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(revision, 99_999_999);
        using var output = new MemoryStream();
        int documentOffset;
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string name, byte[] content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = EntryTimestamp;
                using var destination = entry.Open();
                destination.Write(content);
            }
            Entry("[Content_Types].xml", Encoding.UTF8.GetBytes("""
                <?xml version="1.0" encoding="utf-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="bin" ContentType="application/octet-stream"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>
                """));
            Entry("_rels/.rels", Encoding.UTF8.GetBytes("""
                <?xml version="1.0" encoding="utf-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
                """));
            Entry("word/padding.bin", padding);
            documentOffset = checked((int)output.Position);
            var marker = revision.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>CellBridge synthetic revision " +
                marker + "</w:t></w:r></w:p><w:sectPr/></w:body></w:document>";
            // Whitespace after the root keeps the XML part's configured size for every marker.
            Entry("word/document.xml", Encoding.UTF8.GetBytes(xml.PadRight(ChangedDocumentPartBytes, ' ')));
        }
        return (output.ToArray(), documentOffset);
    }

    private static DataElement Group(ExGuid id, SerialNumber serial, params StreamObject[] objects)
    {
        var writer = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0).Serialize(writer);
        foreach (var item in objects)
        {
            var body = new BinaryWriterEx();
            item.Id.Serialize(body);
            new Compact64bitInt(1).Serialize(body);
            new Compact64bitInt((ulong)item.Bytes.Length).Serialize(body);
            new Compact64bitInt((ulong)item.Children.Length).Serialize(body);
            new Compact64bitInt(0).Serialize(body);
            Record(writer, StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare, body);
        }
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations).Serialize(writer);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupData, 0).Serialize(writer);
        foreach (var item in objects)
        {
            var body = new BinaryWriterEx();
            new Compact64bitInt((ulong)item.Children.Length).Serialize(body);
            foreach (var child in item.Children) child.Serialize(body);
            new Compact64bitInt(0).Serialize(body);
            new BinaryItem(item.Bytes).Serialize(body);
            Record(writer, StreamObjectTypeHeaderStart.ObjectGroupObjectData, body);
        }
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupData).Serialize(writer);
        return new(DataElementType.ObjectGroupDataElementData, id, serial) { Data = writer.ToArray() };
    }

    private static void Record(BinaryWriterEx output, StreamObjectTypeHeaderStart type, BinaryWriterEx body)
    {
        new StreamObjectHeaderStart32Bit(type, body.Length).Serialize(output);
        output.WriteBytes(body.ToArray());
    }

    private sealed record StreamObject(ExGuid Id, byte[] Bytes, ExGuid[] Children);
}

public sealed record IncrementalOfficeSave(FsshttpbCellRequest Request, byte[] Content, byte[] Binary,
    int ChangedChunkBytes, int SentChunks, int ReusedChunks);
