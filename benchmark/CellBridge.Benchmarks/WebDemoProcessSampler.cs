using System.Text.Json.Nodes;
using CellBridge.TestInfrastructure;

internal sealed class WebDemoProcessSampler
{
    internal const int IntervalMilliseconds = 250;
    private readonly Func<OwnedProcessSample> read;
    private readonly long measurementStart, measurementEnd;
    private readonly int resourceProcessId;
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly Dictionary<string, Samples> phases = new(StringComparer.Ordinal);
    private readonly Samples total = new();
    private string? failure;

    internal WebDemoProcessSampler(int resourceProcessId, Func<OwnedProcessSample> read,
        long measurementStart, long measurementEnd)
    {
        this.resourceProcessId = resourceProcessId;
        this.read = read;
        this.measurementStart = measurementStart;
        this.measurementEnd = measurementEnd;
        Capture(); // CPU baseline precedes the first user request.
        worker = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(IntervalMilliseconds));
            try
            {
                while (failure is null && await timer.WaitForNextTickAsync(stop.Token)) Capture();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { if (failure is null) Capture(); }
        });
    }

    private void Capture()
    {
        try
        {
            var sample = read();
            if (total.First is { } first && (sample.ProcessId != first.ProcessId || sample.StartUtc != first.StartUtc ||
                sample.CpuMilliseconds < total.Last!.CpuMilliseconds || sample.TimestampMilliseconds < total.Last.TimestampMilliseconds))
                throw new InvalidOperationException("Process identity or counters changed during sampling.");
            total.Add(sample);
            var phase = sample.TimestampMilliseconds < measurementStart ? "warmup"
                : sample.TimestampMilliseconds < measurementEnd ? "measurement" : "completionDrain";
            if (!phases.TryGetValue(phase, out var samples)) phases.Add(phase, samples = new());
            samples.Add(sample);
        }
        catch (Exception exception) { failure = exception.GetType().Name; }
    }

    internal async Task StopAsync()
    {
        await stop.CancelAsync();
        await worker;
    }

    internal JsonObject Report()
    {
        if (!worker.IsCompleted) throw new InvalidOperationException("Stop process sampling before reading its report.");
        return new()
        {
            ["status"] = failure is null ? "complete" : "failed",
            ["failureType"] = failure,
            ["resourceProcessId"] = resourceProcessId,
            ["applicationProcessId"] = total.First?.ProcessId,
            ["applicationStartUtc"] = total.First?.StartUtc,
            ["identitySource"] = "Fixture-owned Aspire webdemo executable.pid; exact WebDemo executable/DLL command-line match in that process or its explicit procfs child tree. PID and start time checked on every sample.",
            ["scope"] = "Actual WebDemo process only, from first user action through warm-up, measurement and completion drain, before validation. Excludes setup, benchmark runner and database processes.",
            ["interpretation"] = "CPU is cumulative process CPU delta divided by observed wall time; 100 percent means one logical CPU. RSS is sampled working set, and its maximum is not peak allocation or managed heap. Each phase uses its first and last observed samples, so boundary gaps remain only in the total.",
            ["gc"] = "notCollected",
            ["targetIntervalMilliseconds"] = IntervalMilliseconds,
            ["total"] = total.Report(),
            ["phases"] = new JsonObject(phases.Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value.Report()))),
        };
    }

    private sealed class Samples
    {
        internal OwnedProcessSample? First, Last;
        private long count, maximumRss;
        internal void Add(OwnedProcessSample value)
        {
            First ??= value;
            Last = value;
            count++;
            maximumRss = Math.Max(maximumRss, value.WorkingSetBytes);
        }
        internal JsonObject Report()
        {
            var elapsed = First is null ? (long?)null : Last!.TimestampMilliseconds - First.TimestampMilliseconds;
            var cpu = count < 2 ? (double?)null : Last!.CpuMilliseconds - First!.CpuMilliseconds;
            return new()
            {
                ["samples"] = count,
                ["firstSampleUtc"] = First?.CapturedUtc,
                ["lastSampleUtc"] = Last?.CapturedUtc,
                ["observedWallMilliseconds"] = elapsed,
                ["cpuMilliseconds"] = cpu,
                ["cpuPercentOfOneCore"] = elapsed > 0 ? cpu * 100 / elapsed : null,
                ["initialWorkingSetBytes"] = First?.WorkingSetBytes,
                ["finalWorkingSetBytes"] = Last?.WorkingSetBytes,
                ["maximumSampledWorkingSetBytes"] = count > 0 ? maximumRss : null,
            };
        }
    }
}
