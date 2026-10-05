using System.Text.Json;

namespace CellBridge.Storage.Abstractions;

public static class CoordinationFencing
{
    /// <summary>Only authority changes advance the epoch. Graph/content/editor commits alone do not.</summary>
    public static CoordinationState Capture(CoordinationState current, CoordinationState captured) => captured with
    {
        Generation = JsonSerializer.Serialize(current with { Generation = 0 }) ==
            JsonSerializer.Serialize(captured with { Generation = 0 })
            ? current.Generation : checked(current.Generation + 1),
    };
}
