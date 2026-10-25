using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Shared;

// Only these fields participate in namespace admission and lifecycle ancestry.
// Providers project them from authoritative JSON before transferring any rows.
internal sealed record DocumentNamespaceState(Guid ResourceId, long StateVersion, string PathKey, bool IsDeleted,
    long LifecycleGeneration, Guid? ReplacedBy, ImmutableArray<string> RetiredPathKeys)
{
    public static DocumentNamespaceState From(DocumentState state) =>
        new(state.ResourceId, state.StateVersion, state.PathKey, state.IsDeleted,
            state.LifecycleGeneration, state.ReplacedBy, state.RetiredPathKeys);

    public static DocumentNamespaceState Parse(string json, StorageLimits limits)
    {
        try
        {
            var reader = new Utf8JsonReader(DocumentEncoding.Encode(json));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected namespace fields.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int format = 0;
            Guid id = default;
            long version = 0, generation = 1;
            string? path = null;
            bool deleted = false;
            Guid? replacedBy = null;
            var retired = ImmutableArray<string>.Empty;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a namespace property.");
                var name = reader.GetString()!;
                if (!seen.Add(name) || !reader.Read()) throw new JsonException("Duplicate or incomplete namespace property.");
                switch (name)
                {
                    case "FormatVersion": format = reader.GetInt32(); break;
                    case "ResourceId": id = reader.GetGuid(); break;
                    case "StateVersion": version = reader.GetInt64(); break;
                    case "PathKey": path = reader.GetString(); break;
                    case "IsDeleted": deleted = reader.GetBoolean(); break;
                    case "LifecycleGeneration": generation = reader.GetInt64(); break;
                    case "ReplacedBy": replacedBy = reader.TokenType == JsonTokenType.Null ? null : reader.GetGuid(); break;
                    case "RetiredPathKeys":
                        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected retired path keys.");
                        var keys = ImmutableArray.CreateBuilder<string>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (keys.Count >= limits.MaxRetiredPathKeys) throw new JsonException("Too many retired path keys.");
                            var key = reader.GetString();
                            if (string.IsNullOrWhiteSpace(key)) throw new JsonException("Invalid retired path key.");
                            keys.Add(key);
                        }
                        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Incomplete retired path keys.");
                        retired = keys.ToImmutable();
                        break;
                    default: throw new JsonException("Unexpected namespace property.");
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() ||
                !seen.Contains("ResourceId") || !seen.Contains("StateVersion") ||
                format != DocumentState.CurrentFormat || string.IsNullOrWhiteSpace(path) || version < 0 || generation < 1)
                throw new JsonException("Invalid or incomplete namespace fields.");
            return new(id, version, path, deleted, generation, replacedBy, retired);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        { throw new StorageCorruptionException("Invalid persisted namespace fields.", ex); }
    }
}
