namespace CellBridge.Client;

public enum CellBridgeDocumentSessionMode
{
    Reader,
    EagerWriter,
    LazyWriter,
}

public sealed class CellBridgeDocumentSessionOptions
{
    public CellBridgeDocumentSessionMode Mode { get; init; } = CellBridgeDocumentSessionMode.Reader;
    public bool EnableRealTime { get; init; } = true;
    public int LeaseTimeoutSeconds { get; init; } = 3600;
    public TimeSpan LeaseRenewalInterval { get; init; } = TimeSpan.FromMinutes(5);
    public ulong QueryPageBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxQueryPages { get; init; } = 256;
    public int MaxPackageBytes { get; init; } = 128 * 1024 * 1024;
    /// <summary>Maximum serialized size of the graphs retained by one session.</summary>
    public long MaxGraphBytes { get; init; } = 512L * 1024 * 1024;
}

public sealed record CellBridgeDocumentSnapshot(string Revision, byte[] Package);

public enum CellBridgeCommitStatus
{
    Committed,
    Conflict,
    /// <summary>The replay reported a conflict after an earlier attempt lost its response.</summary>
    OutcomeUnknown,
}

public sealed record CellBridgeCommitResult(CellBridgeCommitStatus Status, string Revision)
{
    public bool Succeeded => Status == CellBridgeCommitStatus.Committed;
}

public sealed class CellBridgeProtocolException(string errorCode, string? detail = null)
    : InvalidOperationException(detail is null ? errorCode : $"{errorCode}: {detail}")
{
    public string ErrorCode { get; } = errorCode;
    internal bool IsSoapFault { get; init; }
}
