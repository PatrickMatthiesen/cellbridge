namespace CellBridge.AspNetCore;

public sealed class CellBridgeOptions
{
    /// <summary>Wire-hash configuration, independent of stored-content integrity.</summary>
    public CellBridge.FssHttpB.ProtocolHashingOptions Hashing { get; set; } = CellBridge.FssHttpB.ProtocolHashingOptions.Default;
    public long MaxRequestBytes { get; set; } = 128L * 1024 * 1024;
    public int MaxConcurrentRequests { get; set; } = 8;
    public int MaxMtomParts { get; set; } = 128;
    public int MaxMtomHeaderBytes { get; set; } = 16 * 1024;
    /// <summary>Opt-in raw wire evidence. Null, empty or whitespace disables capture. Restrict directory access and manage retention.</summary>
    public string? CaptureDirectory
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
