using CellBridge.AspNetCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CellBridge.FssHttp.Tests;

public sealed class CaptureStoreFailureTests
{
    [Fact]
    public async Task FailedCreationPreservesExistingEvidenceAndReleasesItsReservation()
    {
        using var capture = new CaptureDirectory();
        await File.WriteAllBytesAsync(capture.Path("existing.bin"), new byte[] { 9 });
        var store = capture.Store(5);

        await store.WriteAsync("existing", ".bin", new byte[] { 1, 2, 3, 4 }, CancellationToken.None);
        await store.WriteAsync("retry", ".bin", new byte[] { 1, 2, 3, 4 }, CancellationToken.None);

        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(capture.Path("existing.bin")));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(capture.Path("retry.bin")));
        Assert.Equal(5, capture.RetainedBytes());
        AssertPrivate(capture.Path("retry.bin"));
    }

    [Fact]
    public async Task FailedWriteRemovesPartialEvidenceAndAllowsAnotherCapture()
    {
        using var capture = new CaptureDirectory();
        var store = capture.Store(4);

        await store.WriteStreamingAsync("failed", ".bin", 4, async stream =>
        {
            await stream.WriteAsync(new byte[] { 1, 2 });
            throw new IOException("Simulated disk write failure.");
        }, CancellationToken.None);
        Assert.False(File.Exists(capture.Path("failed.bin")));

        await store.WriteAsync("retry", ".bin", new byte[] { 3, 4, 5, 6 }, CancellationToken.None);
        Assert.Equal(4, capture.RetainedBytes());
        AssertPrivate(capture.Path("retry.bin"));
    }

    [Fact]
    public async Task CancelledWriteRemovesPartialEvidenceAndReleasesItsReservation()
    {
        using var capture = new CaptureDirectory();
        var store = capture.Store(4);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteStreamingAsync("cancelled", ".bin", 4,
            async stream =>
            {
                await stream.WriteAsync(new byte[] { 1, 2 }, cancellation.Token);
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }, cancellation.Token));
        Assert.False(File.Exists(capture.Path("cancelled.bin")));

        await store.WriteAsync("retry", ".bin", new byte[] { 3, 4, 5, 6 }, CancellationToken.None);
        Assert.Equal(4, capture.RetainedBytes());
        AssertPrivate(capture.Path("retry.bin"));
    }

    [Fact]
    public async Task FailureRollsBackOnlyItsOwnReservationWhileAnotherWriteIsActive()
    {
        using var capture = new CaptureDirectory();
        var store = capture.Store(6);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = store.WriteStreamingAsync("first", ".bin", 4, async stream =>
        {
            await stream.WriteAsync(new byte[] { 1, 2 });
            firstStarted.SetResult();
            await failFirst.Task;
            throw new IOException("Simulated disk write failure.");
        }, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = store.WriteStreamingAsync("second", ".bin", 2, async stream =>
        {
            secondStarted.SetResult();
            await finishSecond.Task;
            await stream.WriteAsync(new byte[] { 3, 4 });
        }, CancellationToken.None);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await store.WriteAsync("no-capacity", ".bin", new byte[] { 7 }, CancellationToken.None);
            Assert.False(File.Exists(capture.Path("no-capacity.bin")));
            failFirst.SetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));

            // The second write still owns two bytes, so five cannot be admitted.
            await store.WriteAsync("too-large", ".bin", new byte[] { 5, 6, 7, 8, 9 }, CancellationToken.None);
            Assert.False(File.Exists(capture.Path("too-large.bin")));
            await store.WriteAsync("retry", ".bin", new byte[] { 5, 6, 7, 8 }, CancellationToken.None);
            finishSecond.SetResult();
            await second.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(File.Exists(capture.Path("first.bin")));
            Assert.Equal(6, capture.RetainedBytes());
            AssertPrivate(capture.Path("retry.bin"));
            AssertPrivate(capture.Path("second.bin"));
        }
        finally
        {
            failFirst.TrySetResult(); finishSecond.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ShortSuccessfulWriteChargesOnlyRetainedBytes()
    {
        using var capture = new CaptureDirectory();
        var store = capture.Store(4);
        await store.WriteStreamingAsync("short", ".bin", 4,
            stream => stream.WriteAsync(new byte[] { 1 }).AsTask(), CancellationToken.None);
        await store.WriteAsync("remaining", ".bin", new byte[] { 2, 3, 4 }, CancellationToken.None);
        Assert.Equal(4, capture.RetainedBytes());
        Assert.Equal(2, Directory.GetFiles(capture.Root).Length);
    }

    private static void AssertPrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    private sealed class CaptureDirectory : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cellbridge-capture-failure-" + Guid.NewGuid().ToString("N"));
        public CaptureDirectory() => Directory.CreateDirectory(Root);
        public string Path(string name) => System.IO.Path.Combine(Root, name);
        public long RetainedBytes() => Directory.GetFiles(Root).Sum(path => new FileInfo(path).Length);
        public ProtocolCaptureStore Store(long budget) => new(new() { CaptureDirectory = Root, MaxCaptureBytes = budget },
            NullLogger<ProtocolCaptureStore>.Instance);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
