namespace CellBridge.DocumentLibrary;

public interface IDestinationFaults
{
    ValueTask BeforePermissionPreparationAsync(Guid resourceId, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    ValueTask AfterManifestOpenedForReadAsync(Guid resourceId, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    ValueTask AfterManifestReplacementAsync(LibraryManifest manifest, CancellationToken cancellationToken);
}

public sealed class NoDestinationFaults : IDestinationFaults
{
    public ValueTask AfterManifestReplacementAsync(LibraryManifest manifest, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class SignalAndWaitDestinationFaults(string signalPath) : IDestinationFaults
{
    public async ValueTask AfterManifestReplacementAsync(LibraryManifest manifest, CancellationToken cancellationToken)
    {
        await using (var signal = new FileStream(signalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            1, FileOptions.WriteThrough))
        {
            signal.WriteByte(1);
            signal.Flush(flushToDisk: true);
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
    }
}
