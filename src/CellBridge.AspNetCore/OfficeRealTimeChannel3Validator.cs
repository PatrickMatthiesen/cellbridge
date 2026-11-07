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

        return OfficeRealTimeExcelNotificationValidator.IsValid(root, 3, ticketClientId);
    }
}
