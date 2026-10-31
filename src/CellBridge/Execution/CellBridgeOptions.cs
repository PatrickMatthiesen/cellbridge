namespace CellBridge.AspNetCore;

public sealed class CellBridgeOptions
{
    /// <summary>Enables Office RTC transport. Set false to disable RTC discovery and endpoints.</summary>
    /// <remarks>Requires stored grants or an immutable versioned policy, without an ordinary async provider or legacy access evaluator.</remarks>
    public bool EnableOfficeRealTime { get; set; } = true;

    /// <summary>Redis primary connection configuration required for RTC when registering multiple instances.</summary>
    public string? OfficeRealTimeRedisConnectionString { get; set; }

    /// <summary>Shared, application-specific RTC namespace. Use the same value on every host for this library.</summary>
    public string? OfficeRealTimeRedisNamespace { get; set; }

    /// <summary>Stable identity of the host's single document catalog. Configure the same GUID on every host and restart to enable Office GetVersions.</summary>
    public Guid? DocumentLibraryId { get; set; }

    internal OfficeDocumentLibrary? DocumentLibrary(string publicBase) => DocumentLibraryId is { } id
        ? new(id, publicBase.TrimEnd('/') + "/_cellbridge/history/settings") : null;
    /// <summary>Wire-hash configuration, independent of stored-content integrity.</summary>
    public CellBridge.FssHttpB.ProtocolHashingOptions Hashing { get; set; } = CellBridge.FssHttpB.ProtocolHashingOptions.Default;
    public long MaxRequestBytes { get; set; } = 128L * 1024 * 1024;
    /// <summary>Maximum active protocol POSTs and document GETs per process, through response delivery.</summary>
    public int MaxConcurrentRequests { get; set; } = 8;
    /// <summary>Maximum admitted requests for one authenticated subject within this process.</summary>
    public int MaxConcurrentRequestsPerSubject { get; set; } = 2;
    /// <summary>Maximum requests waiting for global execution capacity.</summary>
    public int MaxQueuedRequests { get; set; } = 128;
    /// <summary>Maximum requests waiting for one subject's execution capacity.</summary>
    public int MaxQueuedRequestsPerSubject { get; set; } = 32;
    /// <summary>Absolute deadline covering request reading, download reconstruction and response delivery.</summary>
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
