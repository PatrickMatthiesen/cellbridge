using System.Text.Json;

namespace CellBridge.Storage.Abstractions;

public static partial class DocumentStateJson
{
    // Inspect lengths/ranges in the JSON document before materializing descriptors
    // or membership arrays. Compact ranges can represent far more bytes than JSON.
    private static void PreflightGraphMemberships(JsonElement root, StorageLimits limits)
    {
        var compact = UniqueProperty(root, "GraphMetadata", out var metadata);
        var tableLength = 0;
        if (compact)
        {
            if (metadata.ValueKind != JsonValueKind.Object || !UniqueProperty(metadata, "Version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var encoding) || encoding != 1 ||
                !UniqueProperty(metadata, "Elements", out var table) || table.ValueKind != JsonValueKind.Array)
                throw new JsonException("Unsupported or incomplete graph metadata.");
            tableLength = table.GetArrayLength();
            StorageLimits.Check("shared graph descriptors", tableLength,
                checked((long)limits.MaxGraphElements * (limits.MaxHistoryRevisions + 1L)));
        }

        long members = 0;
        CountPartitions(root);
        if (UniqueProperty(root, "Revisions", out var revisions))
        {
            if (revisions.ValueKind != JsonValueKind.Array) throw new JsonException("Expected revision history.");
            StorageLimits.Check("history revisions", revisions.GetArrayLength(), limits.MaxHistoryRevisions);
            foreach (var revision in revisions.EnumerateArray()) CountPartitions(revision);
        }
        StorageLimits.Check("decoded graph membership bytes", checked(members * IntPtr.Size), limits.MaxDecodedGraphMembershipBytes);
        if (compact && tableLength > members) throw new JsonException("Unreferenced shared graph descriptor.");

        void CountPartitions(JsonElement owner)
        {
            if (owner.ValueKind != JsonValueKind.Object || !UniqueProperty(owner, "Partitions", out var partitions) ||
                partitions.ValueKind != JsonValueKind.Array)
                throw new JsonException("Expected document partitions.");
            foreach (var partition in partitions.EnumerateArray())
            {
                if (partition.ValueKind != JsonValueKind.Object || !UniqueProperty(partition, "Elements", out var elements) ||
                    elements.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Expected graph membership.");
                long count = 0;
                if (compact)
                {
                    var ranges = elements.EnumerateArray();
                    while (ranges.MoveNext())
                    {
                        if (ranges.Current.ValueKind != JsonValueKind.Number || !ranges.Current.TryGetInt32(out var start) ||
                            !ranges.MoveNext() || ranges.Current.ValueKind != JsonValueKind.Number ||
                            !ranges.Current.TryGetInt32(out var length) || start < 0 || length <= 0 || start > tableLength - length)
                            throw new JsonException("Invalid graph table range.");
                        Add(length);
                    }
                }
                else Add(elements.GetArrayLength());

                void Add(int length)
                {
                    count = checked(count + length);
                    StorageLimits.Check("graph elements", count, limits.MaxGraphElements);
                    if (compact && count > tableLength) throw new JsonException("Graph membership exceeds the table.");
                    members = checked(members + length);
                    StorageLimits.Check("decoded graph membership bytes", checked(members * IntPtr.Size), limits.MaxDecodedGraphMembershipBytes);
                }
            }
        }
    }

    // Duplicate structural properties could make the serializer materialize an
    // overwritten array that a preflight of only the last property would miss.
    private static bool UniqueProperty(JsonElement owner, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in owner.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) throw new JsonException("Duplicate state property.");
            value = property.Value;
            found = true;
        }
        return found;
    }
}
