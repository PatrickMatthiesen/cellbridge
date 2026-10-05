using System.Buffers.Binary;
using System.Text;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

public sealed partial class PortableArchive
{
    // Bound entry allocation before ZipArchive eagerly creates its central-directory objects.
    // Version 1 accepts only stored/deflated, single-disk, unencrypted ZIP/ZIP64 entries.
    private void CheckZipDirectory(Stream input, bool portable = true)
    {
        var tail = new byte[(int)Math.Min(input.Length, 65557)];
        input.Position = input.Length - tail.Length; input.ReadExactly(tail);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10))
            throw new StorageCorruptionException("Recovery requires a complete single-disk ZIP directory.");
        long count = U16(tail, end + 10), size = U32(tail, end + 12), offset = U32(tail, end + 16);
        long directoryEnd = input.Length - tail.Length + end;
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            if (end < 20 || U32(tail, end - 20) != 0x07064b50 || U32(tail, end - 16) != 0 || U32(tail, end - 4) != 1)
                throw new StorageCorruptionException("Incomplete or multipart ZIP64 directory.");
            long zip64 = checked((long)U64(tail, end - 12));
            if (zip64 < 0 || zip64 > directoryEnd - 76) throw new StorageCorruptionException("Invalid ZIP64 directory offset.");
            input.Position = zip64; var record = new byte[56]; input.ReadExactly(record);
            if (U32(record, 0) != 0x06064b50 || U32(record, 16) != 0 || U32(record, 20) != 0 || U64(record, 24) != U64(record, 32) ||
                checked(zip64 + 12 + (long)U64(record, 4)) != directoryEnd - 20)
                throw new StorageCorruptionException("Invalid ZIP64 directory.");
            count = checked((long)U64(record, 32)); size = checked((long)U64(record, 40)); offset = checked((long)U64(record, 48));
            directoryEnd = zip64;
        }
        StorageLimits.Check("recovery entries", count, _archive.MaxEntries);
        if (offset < 0 || size < 0 || checked(offset + size) != directoryEnd)
            throw new StorageCorruptionException("Invalid recovery central directory boundaries.");
        input.Position = offset;
        var fixedPart = new byte[46]; var names = new HashSet<string>(StringComparer.Ordinal);
        for (long i = 0; i < count; i++)
        {
            if (input.Position > directoryEnd - 46) throw new StorageCorruptionException("Truncated central directory.");
            input.ReadExactly(fixedPart);
            if (U32(fixedPart, 0) != 0x02014b50 || (U16(fixedPart, 8) & (1 | 64 | 8192)) != 0 ||
                U16(fixedPart, 10) is not (0 or 8) || U16(fixedPart, 34) != 0)
                throw new NotSupportedException("Recovery rejects encrypted, multipart or unsupported ZIP compression.");
            int nameLength = U16(fixedPart, 28), extraLength = U16(fixedPart, 30), commentLength = U16(fixedPart, 32);
            if (nameLength < 1 || portable && nameLength > 64 || input.Position + nameLength + extraLength + commentLength > directoryEnd)
                throw new StorageCorruptionException("Invalid recovery ZIP entry metadata.");
            var nameBytes = new byte[nameLength]; input.ReadExactly(nameBytes);
            if (portable && nameBytes.Any(b => b > 127)) throw new StorageCorruptionException("Recovery entry names must be ASCII ordinals.");
            string name = Convert.ToHexString(nameBytes);
            string ordinal = portable ? Encoding.ASCII.GetString(nameBytes) : "";
            if (!names.Add(name) || portable && ordinal != "manifest.json" && !(ordinal.StartsWith("objects/", StringComparison.Ordinal) &&
                ordinal.Length == 16 && ordinal.AsSpan(8).ContainsAnyExceptInRange('0', '9') == false))
                throw new StorageCorruptionException("Duplicate or unexpected recovery ZIP entry name.");
            input.Position += extraLength + commentLength;
        }
        if (input.Position != directoryEnd) throw new StorageCorruptionException("Recovery directory entry count disagrees with its size.");
        input.Position = 0;
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
}
