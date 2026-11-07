using System.Text.Json;

namespace CellBridge.AspNetCore;

// Excel's inner PresenceType is independent of the outer RTC notification channel.
// Validate the supported envelope, not an exact property list: native writers add
// revision and blob metadata. The stores retain the original bounded JSON string.
internal static class OfficeRealTimeExcelNotificationValidator
{
    public static bool IsValid(JsonElement root, int channel, Guid ticketClientId)
    {
        if (!IdentityMatches(root, "A", ticketClientId) ||
            !IdentityMatches(root, "clientId", ticketClientId) ||
            !IdentityMatches(root, "Cid", ticketClientId))
            return false;

        if (!HasInt32(root, "Version", 2) ||
            (!HasInt32(root, "ClientType", 0) && !HasInt32(root, "ClientType", 2)) ||
            !root.TryGetProperty("PresenceType", out var presenceType) ||
            presenceType.ValueKind != JsonValueKind.Number || !presenceType.TryGetInt32(out int type))
            return false;

        if (channel == 8)
            return type == 3 && HasUnsigned(root, "SeqNum", uint.MaxValue) &&
                root.TryGetProperty("SessionId", out var session) && session.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(session.GetString());

        if (channel != 3) return false;
        return type switch
        {
            2 => HasGuid(root, "GeneratedXluid") &&
                root.TryGetProperty("CollabMode", out var mode) && mode.ValueKind == JsonValueKind.Number &&
                mode.TryGetInt32(out _) && root.TryGetProperty("OcsCapable", out var capable) &&
                capable.ValueKind is JsonValueKind.True or JsonValueKind.False,
            5 => HasGuid(root, "FullSaveId"),
            // ClientId is Excel metadata; ticket ownership binds the RTC sender.
            // Excel reserves the all-ones saved revision as an invalid sentinel.
            6 or 7 => HasGuid(root, "ClientId") && HasUnsigned(root, "SavRev", ulong.MaxValue - 1),
            8 => HasGuid(root, "ClientId"),
            9 => HasGuid(root, "Nsv"),
            // The sender routes type 10 here, but its metadata key is not yet resolved.
            10 => true,
            _ => false
        };
    }

    private static bool IdentityMatches(JsonElement root, string name, Guid clientId) =>
        !root.TryGetProperty(name, out var value) ||
        (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) && id == clientId);

    private static bool HasInt32(JsonElement root, string name, int expected) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int actual) && actual == expected;

    private static bool HasUnsigned(JsonElement root, string name, ulong maximum) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetUInt64(out ulong actual) && actual <= maximum;

    private static bool HasGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out Guid id) && id != Guid.Empty;
}
