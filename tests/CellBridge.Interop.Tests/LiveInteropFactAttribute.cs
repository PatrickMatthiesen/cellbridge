namespace CellBridge.Interop.Tests;

/// <summary>Report unconfigured live checks as skipped rather than passing.</summary>
public sealed class LiveInteropFactAttribute : FactAttribute
{
    public LiveInteropFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")))
            Skip = "Set OFFICECOLLABSERVER_INTEROP_ENDPOINT to run against a live server.";
    }
}
