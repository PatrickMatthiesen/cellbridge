using CellBridge.TestInfrastructure;

namespace CellBridge.Benchmarks.Tests;

public sealed class WebDemoProcessSamplerTests
{
    [Fact]
    public async Task ReportsActualCounterDeltasAndSeparatesObservedPhases()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var started = DateTime.UtcNow;
        var sampler = new WebDemoProcessSampler(5, () =>
        {
            var count = Interlocked.Increment(ref calls);
            if (count == 3) ready.TrySetResult();
            return new(7, started, started.AddMilliseconds(count * 10), count * 10, 100 + count * 3, 1000 + count);
        }, 15, 25);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sampler.StopAsync();
        var report = sampler.Report();
        Assert.Equal("complete", report["status"]!.GetValue<string>());
        Assert.Equal(7, report["applicationProcessId"]!.GetValue<int>());
        Assert.Equal(5, report["resourceProcessId"]!.GetValue<int>());
        Assert.Equal("notCollected", report["gc"]!.GetValue<string>());
        Assert.Equal(30d, report["total"]!["cpuPercentOfOneCore"]!.GetValue<double>());
        Assert.Equal((calls - 1) * 3d, report["total"]!["cpuMilliseconds"]!.GetValue<double>());
        Assert.Equal(1000 + calls, report["total"]!["maximumSampledWorkingSetBytes"]!.GetValue<long>());
        Assert.NotNull(report["phases"]!["warmup"]);
        Assert.NotNull(report["phases"]!["measurement"]);
        Assert.NotNull(report["phases"]!["completionDrain"]);
    }

    [Fact]
    public async Task ProcessReuseFailsInsteadOfCombiningUnrelatedCounters()
    {
        var calls = 0;
        var started = DateTime.UtcNow;
        var sampler = new WebDemoProcessSampler(5, () =>
            new(5, started.AddSeconds(Interlocked.Increment(ref calls) - 1), started, calls, calls, 100), 10, 20);
        await sampler.StopAsync();
        var report = sampler.Report();
        Assert.Equal("failed", report["status"]!.GetValue<string>());
        Assert.Equal(1, report["total"]!["samples"]!.GetValue<long>());
        Assert.Equal("InvalidOperationException", report["failureType"]!.GetValue<string>());
    }
}
