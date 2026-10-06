using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using CellBridge.DocumentLibrary;
using CellBridge.Storage.Abstractions;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class DestinationTests
{
    [Fact]
    public async Task ManifestReplacementSucceedsWhileAnotherReaderIsOpen()
    {
        using var files = new TemporaryDirectory();
        var faults = new PauseManifestReadFaults();
        await using var destination = new DocumentLibraryDestination(files.Path, faults);
        var resourceId = Guid.NewGuid();
        await destination.CreateAsync(resourceId, Guid.NewGuid(), "example.docx",
            "/shared/example.docx", new MemoryStream(TestOfficeFile.Docx("baseline")), Permissions());

        faults.Arm();
        var heldRead = destination.GetAsync(resourceId);
        LibraryManifest? replacement = null;
        try
        {
            await faults.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            replacement = await destination.MarkCellBridgeBoundAsync(resourceId)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            faults.Release();
        }

        Assert.True(Assert.IsType<LibraryManifest>(replacement).CellBridgeBound);
        Assert.False((await heldRead).CellBridgeBound);
        Assert.True((await destination.GetAsync(resourceId)).CellBridgeBound);
    }

    [Fact]
    public async Task ReceiptReplayPrecedesRevisionCasAndSurvivesRestart()
    {
        using var files = new TemporaryDirectory();
        var baseline = TestOfficeFile.Docx("baseline");
        var first = TestOfficeFile.Docx("first");
        var second = TestOfficeFile.Docx("second");
        var resourceId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        ExternalDeliveryRequest firstRequest;
        string firstResult;

        await using (var destination = new DocumentLibraryDestination(files.Path))
        {
            var manifest = await destination.CreateAsync(resourceId, bindingId, "example.docx",
                "/shared/example.docx", new MemoryStream(baseline), Permissions());
            firstRequest = Request(resourceId, bindingId, manifest.CurrentRevision, Guid.NewGuid(), 1, 2, first);
            firstResult = (await destination.CompareExchangeAsync(firstRequest, new MemoryStream(first))).Revision!;
            var secondRequest = Request(resourceId, bindingId, firstResult, Guid.NewGuid(), 2, 3, second);
            var secondResult = await destination.CompareExchangeAsync(secondRequest, new MemoryStream(second));
            Assert.Equal(ExternalDeliveryStatus.Applied, secondResult.Status);
            Assert.Equal(second, await destination.ReadCurrentBytesAsync(resourceId));
        }

        await using var restarted = new DocumentLibraryDestination(files.Path);
        var replay = await restarted.CompareExchangeAsync(firstRequest, new MemoryStream(first));
        Assert.Equal(ExternalDeliveryStatus.Applied, replay.Status);
        Assert.Equal(firstResult, replay.Revision);
        Assert.Equal(second, await restarted.ReadCurrentBytesAsync(resourceId));

        var changedFingerprint = firstRequest with
        {
            ExpectedRevision = Guid.NewGuid().ToString("D"),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted
            .CompareExchangeAsync(changedFingerprint, new MemoryStream(first)).AsTask());
    }

    [Fact]
    public async Task StaleRevisionConflictsAndSingleHostLockIsExclusive()
    {
        using var files = new TemporaryDirectory();
        await using var destination = new DocumentLibraryDestination(files.Path);
        Assert.Throws<IOException>(() => new DocumentLibraryDestination(files.Path));
        var resourceId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var baseline = TestOfficeFile.Docx("baseline");
        var manifest = await destination.CreateAsync(resourceId, bindingId, "example.docx",
            "/shared/example.docx", new MemoryStream(baseline), Permissions());
        var appliedBytes = TestOfficeFile.Docx("applied");
        var applied = Request(resourceId, bindingId, manifest.CurrentRevision, Guid.NewGuid(), 1, 2, appliedBytes);
        Assert.Equal(ExternalDeliveryStatus.Applied,
            (await destination.CompareExchangeAsync(applied, new MemoryStream(appliedBytes))).Status);
        var staleBytes = TestOfficeFile.Docx("stale");
        var stale = Request(resourceId, bindingId, manifest.CurrentRevision, Guid.NewGuid(), 2, 3, staleBytes);
        Assert.Equal(ExternalDeliveryStatus.Conflict,
            (await destination.CompareExchangeAsync(stale, new MemoryStream(staleBytes))).Status);
        Assert.Equal(appliedBytes, await destination.ReadCurrentBytesAsync(resourceId));
    }

    [Fact]
    public async Task ProcessDeathAfterManifestReplacementKeepsNewFileAndReceipt()
    {
        using var files = new TemporaryDirectory();
        var resourceId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var baseline = TestOfficeFile.Docx("baseline");
        var changed = TestOfficeFile.Docx("after crash");
        string expectedRevision;
        await using (var destination = new DocumentLibraryDestination(files.Path))
        {
            var manifest = await destination.CreateAsync(resourceId, bindingId, "example.docx",
                "/shared/example.docx", new MemoryStream(baseline), Permissions());
            expectedRevision = manifest.CurrentRevision;
        }
        var input = files.File("changed.docx");
        await File.WriteAllBytesAsync(input, changed);
        var signal = files.File("manifest-replaced.signal");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Probe", "CellBridge.DocumentLibrary.Probe.dll"));
        start.ArgumentList.Add(files.Path);
        start.ArgumentList.Add(resourceId.ToString("D"));
        start.ArgumentList.Add(bindingId.ToString("D"));
        start.ArgumentList.Add(expectedRevision);
        start.ArgumentList.Add(operationId.ToString("D"));
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(signal);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(signal))
            {
                if (process.HasExited)
                    throw new InvalidOperationException("Crash probe exited before its signal: " + await errors);
                await Task.Delay(25, timeout.Token);
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await output;
            await errors;
        }

        await using var restarted = new DocumentLibraryDestination(files.Path);
        var manifestAfterCrash = await restarted.GetAsync(resourceId);
        Assert.NotEqual(expectedRevision, manifestAfterCrash.CurrentRevision);
        Assert.True(manifestAfterCrash.Receipts.ContainsKey(operationId));
        Assert.Equal(changed, await restarted.ReadCurrentBytesAsync(resourceId));
        var replayRequest = Request(resourceId, bindingId, expectedRevision, operationId, 1, 2, changed,
            "probe-" + operationId.ToString("N"));
        var replay = await restarted.CompareExchangeAsync(replayRequest, new MemoryStream(changed));
        Assert.Equal(manifestAfterCrash.CurrentRevision, replay.Revision);
        Assert.Equal(changed, await restarted.ReadCurrentBytesAsync(resourceId));
    }

    private static ImmutableDictionary<string, DocumentAccess> Permissions() =>
        ImmutableDictionary<string, DocumentAccess>.Empty.Add(DocumentLibraryService.OwnerSubject, DocumentAccess.Write);

    internal static ExternalDeliveryRequest Request(Guid resourceId, Guid bindingId, string expectedRevision,
        Guid operationId, long sequence, uint contentVersion, byte[] bytes, string? key = null)
    {
        var handle = new ContentHandle(key ?? "content-" + operationId.ToString("N"), bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return new(bindingId, resourceId.ToString("D"), expectedRevision,
            new(operationId, resourceId, 1, sequence, contentVersion, handle));
    }

    private sealed class PauseManifestReadFaults : IDestinationFaults
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _paused;

        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public async ValueTask AfterManifestOpenedForReadAsync(Guid resourceId,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _armed) == 0 || Interlocked.Exchange(ref _paused, 1) != 0) return;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public ValueTask AfterManifestReplacementAsync(LibraryManifest manifest,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Release() => _release.TrySetResult();
    }
}
