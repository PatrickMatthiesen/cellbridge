using System.Text.Json;

namespace CellBridge.AspNetCore;

internal static class OfficeRealTimeChannel3Validator
{
    public static bool IsValid(JsonElement root, Guid ticketClientId)
    {
        if (root.TryGetProperty("A", out JsonElement sender))
            return sender.ValueKind == JsonValueKind.String &&
                Guid.TryParse(sender.GetString(), out Guid suppliedClientId) &&
                suppliedClientId == ticketClientId;

        if (!root.TryGetProperty("PresenceType", out JsonElement presenceType) ||
            presenceType.ValueKind != JsonValueKind.Number || !presenceType.TryGetInt32(out int type))
            return false;

        return type switch
        {
            2 => HasExcelShape(root, "GeneratedXluid", requireCollabMode: true),
            5 => HasExcelShape(root, "FullSaveId", requireCollabMode: false),
            _ => false
        };
    }

    private static bool HasExcelShape(JsonElement root, string guidField, bool requireCollabMode)
    {
        int expectedPropertyCount = requireCollabMode ? 6 : 4;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != expectedPropertyCount ||
            !HasInt32(root, "Version", 2) || !HasInt32(root, "ClientType", 0) ||
            !HasGuid(root, guidField))
            return false;

        if (requireCollabMode)
        {
            if (!HasInt32(root, "CollabMode", 0) ||
                !root.TryGetProperty("OcsCapable", out JsonElement ocsCapable) ||
                ocsCapable.ValueKind != JsonValueKind.True)
                return false;
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name is not ("Version" or "ClientType" or "PresenceType" or "GeneratedXluid" or
                "CollabMode" or "OcsCapable" or "FullSaveId"))
                return false;
        }

        return true;
    }

    private static bool HasInt32(JsonElement root, string name, int expected) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int actual) && actual == expected;

    private static bool HasGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out Guid id) && id != Guid.Empty;
}
