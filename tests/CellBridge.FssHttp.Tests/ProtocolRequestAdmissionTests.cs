using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class ProtocolRequestAdmissionTests
{
    [Fact]
    public async Task HostDisposalDisposesItsAdmissionLimiter()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCellBridge(TestStorage.Memory(), requireDurability: false);
        var app = builder.Build();
        var admission = app.Services.GetRequiredService<ProtocolRequestAdmission>();
        try
        {
            using var active = (await admission.AcquireAsync("subject-a")).Lease!;
            active.Dispose();
            await app.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => admission.AcquireAsync("subject-a").AsTask());
        }
        finally
        {
            await app.DisposeAsync();
            admission.Dispose();
        }
    }

    [Fact]
    public async Task QueuedSubjectRequestEntersAfterLeaseRelease()
    {
        using var admission = Create(maxConcurrent: 2, maxPerSubject: 1, maxQueued: 2, maxQueuedPerSubject: 1);
        var first = await admission.AcquireAsync("subject-a");
        Assert.Equal(0, first.RejectionStatus);

        var waiting = admission.AcquireAsync("subject-a").AsTask();
        Assert.False(waiting.IsCompleted);

        first.Lease!.Dispose();
        var next = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, next.RejectionStatus);
        next.Lease!.Dispose();
    }

    [Fact]
    public async Task FullSubjectQueueReturns429AndReleasesNoPermit()
    {
        using var admission = Create(maxConcurrent: 3, maxPerSubject: 1, maxQueued: 2, maxQueuedPerSubject: 1);
        var active = await admission.AcquireAsync("subject-a");
        var queued = admission.AcquireAsync("subject-a").AsTask();

        var overflow = await admission.AcquireAsync("subject-a");
        Assert.Null(overflow.Lease);
        Assert.Equal(429, overflow.RejectionStatus);

        active.Lease!.Dispose();
        var queuedResult = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, queuedResult.RejectionStatus);
        queuedResult.Lease!.Dispose();

        var after = await admission.AcquireAsync("subject-a");
        Assert.Equal(0, after.RejectionStatus);
        after.Lease!.Dispose();
    }

    [Fact]
    public async Task FullGlobalQueueReturns503AndDoesNotLeakSubjectPermit()
    {
        using var admission = Create(maxConcurrent: 1, maxPerSubject: 2, maxQueued: 1, maxQueuedPerSubject: 2);
        var active = await admission.AcquireAsync("subject-a");
        var queued = admission.AcquireAsync("subject-b").AsTask();
        Assert.False(queued.IsCompleted);

        var overflow = await admission.AcquireAsync("subject-c");
        Assert.Null(overflow.Lease);
        Assert.Equal(503, overflow.RejectionStatus);

        active.Lease!.Dispose();
        var queuedResult = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, queuedResult.RejectionStatus);
        queuedResult.Lease!.Dispose();

        var after = await admission.AcquireAsync("subject-c");
        Assert.Equal(0, after.RejectionStatus);
        after.Lease!.Dispose();
    }

    [Fact]
    public async Task CancelledSubjectWaitDoesNotLeakPermit()
    {
        using var admission = Create(maxConcurrent: 1, maxPerSubject: 1, maxQueued: 1, maxQueuedPerSubject: 1);
        var active = await admission.AcquireAsync("subject-a");
        using var cancellation = new CancellationTokenSource();
        var waiting = admission.AcquireAsync("subject-a", cancellation.Token).AsTask();
        Assert.False(waiting.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        active.Lease!.Dispose();
        var after = await admission.AcquireAsync("subject-a");
        Assert.Equal(0, after.RejectionStatus);
        after.Lease!.Dispose();
    }

    [Fact]
    public async Task CancelledGlobalWaitReleasesItsSubjectPermit()
    {
        using var admission = Create(maxConcurrent: 1, maxPerSubject: 1, maxQueued: 1, maxQueuedPerSubject: 1);
        var active = await admission.AcquireAsync("subject-a");
        using var cancellation = new CancellationTokenSource();
        var waiting = admission.AcquireAsync("subject-b", cancellation.Token).AsTask();
        Assert.False(waiting.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        active.Lease!.Dispose();

        var after = await admission.AcquireAsync("subject-b");
        Assert.Equal(0, after.RejectionStatus);
        after.Lease!.Dispose();
    }

    [Fact]
    public async Task ZeroQueueLimitsRetainFailFastRejections()
    {
        using var admission = Create(maxConcurrent: 1, maxPerSubject: 1, maxQueued: 0, maxQueuedPerSubject: 0);
        var active = await admission.AcquireAsync("subject-a");

        var subjectLimit = await admission.AcquireAsync("subject-a");
        Assert.Equal(429, subjectLimit.RejectionStatus);
        Assert.Null(subjectLimit.Lease);
        var globalLimit = await admission.AcquireAsync("subject-b");
        Assert.Equal(503, globalLimit.RejectionStatus);
        Assert.Null(globalLimit.Lease);

        active.Lease!.Dispose();
    }

    [Fact]
    public async Task SubjectsAreIsolatedAndGlobalActiveLimitStillApplies()
    {
        using var admission = Create(maxConcurrent: 2, maxPerSubject: 1, maxQueued: 1, maxQueuedPerSubject: 1);
        var a = await admission.AcquireAsync("subject-a");
        Assert.Equal(0, a.RejectionStatus);

        var subjectAWaiter = admission.AcquireAsync("subject-a").AsTask();
        Assert.False(subjectAWaiter.IsCompleted);

        // Subject A's queue does not hold global capacity while it waits. Subject B can use
        // the other process-wide permit immediately.
        var b = await admission.AcquireAsync("subject-b");
        Assert.Equal(0, b.RejectionStatus);
        Assert.False(subjectAWaiter.IsCompleted);

        a.Lease!.Dispose();
        var aNext = await subjectAWaiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, aNext.RejectionStatus);
        b.Lease!.Dispose();
        aNext.Lease!.Dispose();
    }

    [Fact]
    public async Task LeaseDisposeIsIdempotent()
    {
        using var admission = Create(maxConcurrent: 1, maxPerSubject: 1, maxQueued: 0, maxQueuedPerSubject: 0);
        var acquired = await admission.AcquireAsync("subject-a");

        acquired.Lease!.Dispose();
        acquired.Lease.Dispose();

        var next = await admission.AcquireAsync("subject-a");
        Assert.Equal(0, next.RejectionStatus);
        next.Lease!.Dispose();
    }

    private static ProtocolRequestAdmission Create(int maxConcurrent, int maxPerSubject,
        int maxQueued, int maxQueuedPerSubject) => new(new CellBridgeOptions
    {
        MaxConcurrentRequests = maxConcurrent,
        MaxConcurrentRequestsPerSubject = maxPerSubject,
        MaxQueuedRequests = maxQueued,
        MaxQueuedRequestsPerSubject = maxQueuedPerSubject,
    });
}
