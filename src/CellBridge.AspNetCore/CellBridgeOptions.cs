namespace CellBridge.AspNetCore;

public sealed class CellBridgeOptions
{
    public long MaxRequestBytes { get; set; } = 128L * 1024 * 1024;
    /// <summary>Opt-in raw wire evidence. Null disables capture. Restrict directory access and manage retention.</summary>
    public string? CaptureDirectory { get; set; }
}
