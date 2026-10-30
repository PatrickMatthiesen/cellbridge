using CellBridge.FssHttpB;

namespace CellBridge.AspNetCore;

internal static class OfficeHistoryVersion
{
    public static bool TryRead(QueryChangesVersion version, out uint major, out uint minor)
    {
        major = minor = 0;
        if (version.MajorVersion is { } numeric && version.MinorVersion is { } numericMinor)
        {
            if (version.VersionToken.Length != 0) return false;
            major = numeric; minor = numericMinor;
            return true;
        }
        if (version.MajorVersion is not null || version.MinorVersion is not null) return false;
        // Microsoft 365 16.0.20527's observed tokens match the
        // Versioning/GetVersionList number as compact major, compact minor, zero.
        // Word's observed 2.0 and 1.0 selections are 05 00 00 and 03 00 00.
        // Accept only this exact canonical form, never arbitrary token bytes.
        try
        {
            var reader = new BinaryReaderEx(version.VersionToken);
            ulong first = Compact64bitInt.Deserialize(reader).Value;
            ulong second = Compact64bitInt.Deserialize(reader).Value;
            if (first > uint.MaxValue || second > uint.MaxValue || reader.Remaining != 1 || reader.ReadByte() != 0)
                return false;
            var canonical = new BinaryWriterEx();
            new Compact64bitInt(first).Serialize(canonical);
            new Compact64bitInt(second).Serialize(canonical);
            canonical.WriteByte(0);
            if (!canonical.ToArray().AsSpan().SequenceEqual(version.VersionToken)) return false;
            major = (uint)first; minor = (uint)second;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        { return false; }
    }
}
