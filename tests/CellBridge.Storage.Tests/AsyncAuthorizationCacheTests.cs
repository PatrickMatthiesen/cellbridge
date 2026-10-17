using CellBridge.AspNetCore;
using CellBridge.Authorization.Internal;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Tests;

public sealed class AsyncAuthorizationCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task HighCardinalityAllowDenyAndInvalidationChurnRetainsOnlyBoundedMetadata()
    {
        var cache = new BoundedAuthorizationCache<bool>(new(32, 1024, TimeSpan.FromMinutes(1)), () => false);
        for (int index = 0; index < 20_000; index++)
        {
            var key = new AuthorizationCacheKey("domain", "subject-" + index, "tenant", "workspace-" + index,
                "resource-" + index, "1", "read");
            bool allowed = index % 2 == 0;
            Assert.Equal(allowed, await cache.AuthorizeAsync(key,
                _ => ValueTask.FromResult(new AuthorizationCacheDecision<bool>(allowed, allowed))));
            cache.InvalidateSubject("absent-subject-" + index);
            cache.InvalidateResource("absent-resource-" + index);
            cache.InvalidateWorkspace("absent-workspace-" + index);
            var counts = cache.Counts;
            Assert.InRange(counts.Entries, 0, 32);
            Assert.Equal(counts.Entries, counts.LruNodes);
        }
        Assert.Equal(32, cache.Counts.Entries);
    }

    [Fact]
    public async Task CacheExpiresAtTheShortestSourceOrLocalDeadline()
    {
        var clock = new ManualTimeProvider(Start);
        var sourceDecision = new DocumentAuthorizationDecision(true, Start.AddSeconds(-5), Start.AddHours(1));
        var provider = new DelegateProvider((call, _, _) => ValueTask.FromResult(
            call == 1 ? sourceDecision : new DocumentAuthorizationDecision(false)));
        var cache = Create(provider, clock, ttl: TimeSpan.FromSeconds(10));
        var request = Request(Guid.NewGuid());

        Assert.Same(sourceDecision, await cache.AuthorizeAsync(request));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Same(sourceDecision, await cache.AuthorizeAsync(request));
        Assert.Equal(1, provider.Calls);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False((await cache.AuthorizeAsync(request)).Allowed);
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackTimeConsumesTheLocalLifetime(bool suppliesLongSourceDeadline)
    {
        var clock = new ManualTimeProvider(Start);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateProvider(async (_, _, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new(true, FreshUntilUtc: suppliesLongSourceDeadline ? Start.AddHours(1) : null);
        });
        var cache = Create(provider, clock, ttl: TimeSpan.FromSeconds(10));

        var pending = cache.AuthorizeAsync(Request(Guid.NewGuid())).AsTask();
        await started.Task;
        clock.Advance(TimeSpan.FromSeconds(11));
        release.TrySetResult();

        Assert.False((await pending).Allowed);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task InvalidationDuringEvaluationDiscardsThePendingAllow()
    {
        var clock = new ManualTimeProvider(Start);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateProvider(async (call, _, _) =>
        {
            if (call == 1)
            {
                started.TrySetResult();
                await release.Task;
                return new(true);
            }
            return new(false);
        });
        var cache = Create(provider, clock);
        var request = Request(Guid.NewGuid());

        var pending = cache.AuthorizeAsync(request).AsTask();
        await started.Task;
        cache.InvalidateSubject(request.Subject);
        release.TrySetResult();

        Assert.False((await pending).Allowed);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task SuccessfulCoalescedWaiterDoesNotConsumeInvalidationRetryBudget()
    {
        var clock = new ManualTimeProvider(Start);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateProvider(async (_, _, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new(true);
        });
        var cache = Create(provider, clock, maximumInvalidationRetries: 0);
        var request = Request(Guid.NewGuid());

        var first = cache.AuthorizeAsync(request).AsTask();
        await started.Task;
        var second = cache.AuthorizeAsync(request).AsTask();
        release.TrySetResult();

        Assert.All(await Task.WhenAll(first, second), decision => Assert.True(decision.Allowed));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ResultCreatedBeforeInvalidationIsNotAdmittedAfterIt()
    {
        var clock = new ManualTimeProvider(Start);
        CachedDocumentAuthorizationProvider? cache = null;
        var provider = new DelegateProvider((call, request, _) =>
        {
            if (call != 1) return ValueTask.FromResult(new DocumentAuthorizationDecision(false));
            var completedResult = new DocumentAuthorizationDecision(true);
            cache!.InvalidateSubject(request.Subject);
            return ValueTask.FromResult(completedResult);
        });
        cache = Create(provider, clock);

        var decision = await cache.AuthorizeAsync(Request(Guid.NewGuid()));

        Assert.False(decision.Allowed);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ContinuousInvalidationStopsAfterTheConfiguredAttempts()
    {
        var clock = new ManualTimeProvider(Start);
        CachedDocumentAuthorizationProvider? cache = null;
        var provider = new DelegateProvider((_, request, _) =>
        {
            cache!.InvalidateSubject(request.Subject);
            return ValueTask.FromResult(new DocumentAuthorizationDecision(true));
        });
        cache = Create(provider, clock, maximumInvalidationRetries: 2);

        Assert.False((await cache.AuthorizeAsync(Request(Guid.NewGuid()))).Allowed);
        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task InvalidOrExpiredProviderMetadataFailsClosedAndIsNotCached()
    {
        var clock = new ManualTimeProvider(Start);
        var provider = new DelegateProvider((call, _, _) => ValueTask.FromResult(call switch
        {
            1 => new DocumentAuthorizationDecision(true, Start.AddSeconds(1)),
            2 => new DocumentAuthorizationDecision(true, Start, Start),
            _ => new DocumentAuthorizationDecision(true, Start.AddMinutes(-1), Start.AddTicks(-1))
        }));
        var cache = Create(provider, clock);
        var request = Request(Guid.NewGuid());

        Assert.False((await cache.AuthorizeAsync(request)).Allowed);
        Assert.False((await cache.AuthorizeAsync(request)).Allowed);
        Assert.False((await cache.AuthorizeAsync(request)).Allowed);
        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task BackwardWallClockChangeDoesNotExtendAReusedAllow()
    {
        var clock = new ManualTimeProvider(Start);
        var provider = new DelegateProvider((call, _, _) => ValueTask.FromResult(new DocumentAuthorizationDecision(call == 1)));
        var cache = Create(provider, clock, ttl: TimeSpan.FromSeconds(10));
        var request = Request(Guid.NewGuid());

        Assert.True((await cache.AuthorizeAsync(request)).Allowed);
        clock.MoveWallClock(TimeSpan.FromDays(-1));
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(10));

        Assert.False((await cache.AuthorizeAsync(request)).Allowed);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ForwardWallClockChangeExpiresTheSourceDeadline()
    {
        var clock = new ManualTimeProvider(Start);
        var provider = new DelegateProvider((call, _, _) => ValueTask.FromResult(call == 1
            ? new DocumentAuthorizationDecision(true, Start, Start.AddSeconds(5))
            : new DocumentAuthorizationDecision(false)));
        var cache = Create(provider, clock, ttl: TimeSpan.FromMinutes(1));
        var request = Request(Guid.NewGuid());

        Assert.True((await cache.AuthorizeAsync(request)).Allowed);
        clock.MoveWallClock(TimeSpan.FromSeconds(5));

        Assert.False((await cache.AuthorizeAsync(request)).Allowed);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CapacityEvictionDoesNotRejectACompletedPendingDecision()
    {
        var clock = new ManualTimeProvider(Start);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstId = Guid.NewGuid();
        var provider = new DelegateProvider(async (_, request, _) =>
        {
            if (request.ResourceId == firstId)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            return new(true);
        });
        var cache = Create(provider, clock, capacity: 1);

        var pending = cache.AuthorizeAsync(Request(firstId)).AsTask();
        await firstStarted.Task;
        Assert.True((await cache.AuthorizeAsync(Request(Guid.NewGuid()))).Allowed);
        releaseFirst.TrySetResult();

        Assert.True((await pending).Allowed);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CapacityAndLocalInvalidationBoundMetadataChurn()
    {
        var clock = new ManualTimeProvider(Start);
        var provider = new DelegateProvider((_, _, _) => ValueTask.FromResult(new DocumentAuthorizationDecision(true)));
        var cache = Create(provider, clock, capacity: 2);
        var first = Request(Guid.NewGuid());
        var second = Request(Guid.NewGuid());
        var third = Request(Guid.NewGuid());

        await cache.AuthorizeAsync(first);
        await cache.AuthorizeAsync(second);
        await cache.AuthorizeAsync(first);
        await cache.AuthorizeAsync(third);
        await cache.AuthorizeAsync(second);
        Assert.Equal(4, provider.Calls);

        cache.InvalidateSubject(first.Subject);
        await cache.AuthorizeAsync(second);
        cache.InvalidateResource(third.ResourceId);
        await cache.AuthorizeAsync(third);
        cache.InvalidateWorkspace(first.Scope.WorkspaceId!);
        await cache.AuthorizeAsync(second);
        Assert.Equal(7, provider.Calls);

        await cache.AuthorizeAsync(second with { Binding = new("documents", 1, 2) });
        await cache.AuthorizeAsync(second with { LifecycleGeneration = 2 });
        Assert.Equal(9, provider.Calls);
    }

    [Fact]
    public async Task ProviderFailureCannotReuseOrCreateAnAllow()
    {
        var clock = new ManualTimeProvider(Start);
        var provider = new DelegateProvider((call, _, _) => call switch
        {
            1 => ValueTask.FromResult(new DocumentAuthorizationDecision(true)),
            2 => ValueTask.FromException<DocumentAuthorizationDecision>(new IOException("authority unavailable")),
            _ => ValueTask.FromResult(new DocumentAuthorizationDecision(true))
        });
        var cache = Create(provider, clock);
        var request = Request(Guid.NewGuid());

        Assert.True((await cache.AuthorizeAsync(request)).Allowed);
        cache.InvalidateResource(request.ResourceId);
        Assert.False((await cache.AuthorizeAsync(request)).Allowed);
        Assert.True((await cache.AuthorizeAsync(request)).Allowed);
        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task OversizedKeyFailsClosedWithoutCallingTheProvider()
    {
        var clock = new ManualTimeProvider(Start);
        var provider = new DelegateProvider((_, _, _) => ValueTask.FromResult(new DocumentAuthorizationDecision(true)));
        var cache = Create(provider, clock, maximumKeyLength: 8);

        Assert.False((await cache.AuthorizeAsync(Request(Guid.NewGuid()))).Allowed);
        Assert.Equal(0, provider.Calls);
    }

    private static CachedDocumentAuthorizationProvider Create(
        IDocumentAuthorizationProvider provider,
        TimeProvider clock,
        int capacity = 32,
        int maximumKeyLength = 2048,
        TimeSpan? ttl = null,
        int maximumInvalidationRetries = 2) => new(provider, new()
        {
            Capacity = capacity,
            MaximumKeyLength = maximumKeyLength,
            DecisionTtl = ttl ?? TimeSpan.FromMinutes(1),
            MaximumInvalidationRetries = maximumInvalidationRetries
        }, clock);

    private static DocumentAuthorizationRequest Request(Guid resourceId) => new(
        "subject",
        new("tenant", "workspace"),
        resourceId,
        1,
        new DocumentAuthorizationBinding("documents", 1, 1),
        DocumentAction.Read);

    private sealed class DelegateProvider(
        Func<int, DocumentAuthorizationRequest, CancellationToken, ValueTask<DocumentAuthorizationDecision>> authorize)
        : IDocumentAuthorizationProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string PolicyDomain => "documents";

        public ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(
            DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            authorize(Interlocked.Increment(ref _calls), request, cancellationToken);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_gate) return _timestamp;
        }

        internal void Advance(TimeSpan duration)
        {
            lock (_gate)
            {
                _utcNow += duration;
                _timestamp += duration.Ticks;
            }
        }

        internal void AdvanceMonotonic(TimeSpan duration)
        {
            lock (_gate) _timestamp += duration.Ticks;
        }

        internal void MoveWallClock(TimeSpan duration)
        {
            lock (_gate) _utcNow += duration;
        }
    }
}
