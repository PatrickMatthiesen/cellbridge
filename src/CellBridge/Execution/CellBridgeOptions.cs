namespace CellBridge.AspNetCore;

public sealed class CellBridgeOptions
{
    /// <summary>Wire-hash configuration, independent of stored-content integrity.</summary>
    public CellBridge.FssHttpB.ProtocolHashingOptions Hashing { get; set; } = CellBridge.FssHttpB.ProtocolHashingOptions.Default;
    public long MaxRequestBytes { get; set; } = 128L * 1024 * 1024;
    public int MaxConcurrentRequests { get; set; } = 8;
    public int MaxConcurrentRequestsPerSubject { get; set; } = 2;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Fixed external HTTPS origin. Required for remote hosts unless AllowedHosts validates their authority.</summary>
    public string? PublicOrigin { get; set; }
    /// <summary>Total bytes retained by this process in its capture directory, including existing evidence.</summary>
    public long MaxCaptureBytes { get; set; } = 1024L * 1024 * 1024;
    public int MaxMtomParts { get; set; } = 128;
    public int MaxMtomHeaderBytes { get; set; } = 16 * 1024;
    /// <summary>Opt-in raw wire evidence. Null, empty or whitespace disables capture. Restrict directory access and manage retention.</summary>
    public string? CaptureDirectory
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
