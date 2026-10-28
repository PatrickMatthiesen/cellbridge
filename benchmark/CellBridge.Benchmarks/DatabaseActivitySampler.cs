using System.Diagnostics;
using System.Text.Json.Nodes;

// One background owner aggregates samples, then readers inspect the report after StopAsync.
internal sealed class DatabaseActivitySampler
{
    internal const int IntervalMilliseconds = 250;
    private const int MaximumGroups = 128;
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private readonly Task worker;
    private readonly Dictionary<string, ActivityGroup> groups = new(StringComparer.Ordinal);
    private readonly DateTime startedUtc = DateTime.UtcNow;
    private DateTime stoppedUtc;
    private DateTime? firstSampleStartedUtc;
    private DateTime? lastSampleStartedUtc;
    private long samples;
    private long blockedBackendObservations;
    private long maximumBlockedBackends;
    private double intervalTotalMilliseconds;
    private double maximumIntervalMilliseconds;
    private double maximumQueryMilliseconds;
    private string? failureType;
    private bool cancelled;

    internal DatabaseActivitySampler(Func<CancellationToken, Task<JsonArray>> readSample,
        Func<ValueTask> disposeConnection, CancellationToken workloadCancellation)
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(workloadCancellation);
        var token = cancellation.Token;
        worker = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(IntervalMilliseconds));
                long previous = 0;
                while (!token.IsCancellationRequested)
                {
                    var sampleStartedUtc = DateTime.UtcNow;
                    var started = Stopwatch.GetTimestamp();
                    var rows = await readSample(token);
                    firstSampleStartedUtc ??= sampleStartedUtc;
                    lastSampleStartedUtc = sampleStartedUtc;
                    maximumQueryMilliseconds = Math.Max(maximumQueryMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    if (previous != 0)
                    {
                        var interval = Stopwatch.GetElapsedTime(previous, started).TotalMilliseconds;
                        intervalTotalMilliseconds += interval;
                        maximumIntervalMilliseconds = Math.Max(maximumIntervalMilliseconds, interval);
                    }
                    previous = started;
                    AddSample(rows);
                    if (!await timer.WaitForNextTickAsync(token)) break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; }
            // Diagnostic failure must not replace a workload failure or leak SQL/connection details.
            catch (Exception exception) { failureType = exception.GetType().Name; }
            finally
            {
                try { await disposeConnection(); }
                catch (Exception exception) { failureType ??= exception.GetType().Name; }
                stoppedUtc = DateTime.UtcNow;
            }
        });
    }

    internal async Task StopAsync()
    {
        CancellationTokenSource? owned;
        string? cancellationFailure = null;
        lock (gate)
        {
            owned = cancellation;
            cancellation = null;
            try { owned?.Cancel(); }
            catch (Exception exception) { cancellationFailure = exception.GetType().Name; }
        }
        try { await worker; }
        finally { owned?.Dispose(); }
        failureType ??= cancellationFailure;
    }

    private void AddSample(JsonArray rows)
    {
        var current = new Dictionary<string, ActivityGroup>(StringComparer.Ordinal);
        foreach (var row in rows.OfType<JsonObject>())
        {
            var state = row["state"]?.GetValue<string>() ?? "unknown";
            var waitType = row["wait_event_type"]?.GetValue<string>() ?? "none";
            var waitEvent = row["wait_event"]?.GetValue<string>() ?? "none";
            var queryClass = row["query_class"]!.GetValue<string>();
            var key = $"{state}/{waitType}/{waitEvent}/{queryClass}";
            if (!groups.ContainsKey(key) && !current.ContainsKey(key) && groups.Count + current.Keys.Count(k => !groups.ContainsKey(k)) >= MaximumGroups)
            {
                key = "overflow";
                state = waitType = waitEvent = queryClass = "overflow";
            }
            if (!current.TryGetValue(key, out var group))
                current.Add(key, group = new(state, waitType, waitEvent, queryClass));
            group.Backends += row["backends"]!.GetValue<long>();
            group.BlockedBackends += row["blocked_backends"]!.GetValue<long>();
            group.BlockingPids += row["blocking_pids"]!.GetValue<long>();
        }
        samples++;
        var blocked = current.Values.Sum(group => group.BlockedBackends);
        blockedBackendObservations += blocked;
        maximumBlockedBackends = Math.Max(maximumBlockedBackends, blocked);
        foreach (var (key, sample) in current)
        {
            if (!groups.TryGetValue(key, out var total))
                groups.Add(key, total = new(sample.State, sample.WaitType, sample.WaitEvent, sample.QueryClass));
            total.SamplesPresent++;
            total.Backends += sample.Backends;
            total.BlockedBackends += sample.BlockedBackends;
            total.BlockingPids += sample.BlockingPids;
            total.MaximumBackends = Math.Max(total.MaximumBackends, sample.Backends);
            total.MaximumBlockedBackends = Math.Max(total.MaximumBlockedBackends, sample.BlockedBackends);
        }
    }

    internal JsonObject Report()
    {
        if (!worker.IsCompleted) throw new InvalidOperationException("Stop activity sampling before reading its report.");
        return new JsonObject
        {
            ["status"] = failureType is null ? "complete" : "failed",
            ["failureType"] = failureType,
            ["cancellationObserved"] = cancelled,
            ["scope"] = "Warm-up, measurement and completion drain only, from the first user action until pre-validation capture. All client backends in the owned cellbridge database, excluding the sampler; includes Identity and health checks.",
            ["interpretation"] = "Point-in-time backend observations, not unique waits or lock-hold durations. Short waits can be missed. Idle Client waits are separate from active query waits. Quota means the active query references cellbridge_usage; this does not identify every quota transaction or prove the blocking relation.",
            ["privacy"] = "Only state, wait category/event, quota/other classification and counts are retained. No query text, backend IDs, identities or connection strings.",
            ["startedUtc"] = startedUtc,
            ["stoppedUtc"] = stoppedUtc,
            ["firstSuccessfulSampleStartedUtc"] = firstSampleStartedUtc,
            ["lastSuccessfulSampleStartedUtc"] = lastSampleStartedUtc,
            ["targetIntervalMilliseconds"] = IntervalMilliseconds,
            ["sampleCount"] = samples,
            ["meanObservedIntervalMilliseconds"] = samples > 1 ? intervalTotalMilliseconds / (samples - 1) : null,
            ["maximumObservedIntervalMilliseconds"] = samples > 1 ? maximumIntervalMilliseconds : null,
            ["maximumSampleQueryMilliseconds"] = maximumQueryMilliseconds,
            ["blockedBackendObservations"] = blockedBackendObservations,
            ["maximumBlockedBackends"] = maximumBlockedBackends,
            ["maximumRetainedGroups"] = MaximumGroups + 1,
            ["groups"] = new JsonArray(groups.Values.Select(group => (JsonNode)new JsonObject
            {
                ["state"] = group.State,
                ["waitEventType"] = group.WaitType,
                ["waitEvent"] = group.WaitEvent,
                ["queryClass"] = group.QueryClass,
                ["samplesPresent"] = group.SamplesPresent,
                ["backendObservations"] = group.Backends,
                ["blockedBackendObservations"] = group.BlockedBackends,
                ["blockingPidObservations"] = group.BlockingPids,
                ["maximumBackends"] = group.MaximumBackends,
                ["maximumBlockedBackends"] = group.MaximumBlockedBackends,
            }).ToArray()),
        };
    }

    private sealed class ActivityGroup(string state, string waitType, string waitEvent, string queryClass)
    {
        internal string State { get; } = state;
        internal string WaitType { get; } = waitType;
        internal string WaitEvent { get; } = waitEvent;
        internal string QueryClass { get; } = queryClass;
        internal long SamplesPresent, Backends, BlockedBackends, BlockingPids, MaximumBackends, MaximumBlockedBackends;
    }
}
