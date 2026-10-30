using System.Text.Json.Nodes;

namespace CellBridge.Benchmarks.Tests;

public sealed class DatabaseActivitySamplerTests
{
    [Fact]
    public async Task Stop_cancels_pending_read_and_disposes_connection_once()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposals = 0;
        var sampler = new DatabaseActivitySampler(async token =>
        {
            reading.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        }, () => { disposals++; return ValueTask.CompletedTask; }, CancellationToken.None);

        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sampler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await sampler.StopAsync();

        Assert.Equal(1, disposals);
        Assert.True(sampler.Report()["cancellationObserved"]!.GetValue<bool>());
        Assert.Equal(0, sampler.Report()["sampleCount"]!.GetValue<long>());
    }

    [Fact]
    public async Task Read_and_disposal_failures_are_reported_without_exception_details()
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sampler = new DatabaseActivitySampler(_ => Task.FromException<JsonArray>(
            new InvalidDataException("Password=secret; SELECT private_identity")), () =>
        {
            disposed.SetResult();
            throw new IOException("Sensitive disposal details");
        }, CancellationToken.None);

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sampler.StopAsync();
        var report = sampler.Report();

        Assert.Equal("failed", report["status"]!.GetValue<string>());
        Assert.Equal(nameof(InvalidDataException), report["failureType"]!.GetValue<string>());
        Assert.DoesNotContain("secret", report.ToJsonString());
        Assert.DoesNotContain("private_identity", report.ToJsonString());
        Assert.DoesNotContain("Sensitive", report.ToJsonString());
    }

    [Fact]
    public async Task Aggregation_retains_a_bounded_number_of_groups_and_all_blocked_counts()
    {
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rows = new JsonArray(Enumerable.Range(0, 256).Select(index => (JsonNode)new JsonObject
        {
            ["state"] = "state" + index,
            ["wait_event_type"] = "Lock",
            ["wait_event"] = "transactionid",
            ["query_class"] = "quota",
            ["backends"] = 2L,
            ["blocked_backends"] = 1L,
            ["blocking_pids"] = 1L,
        }).ToArray());
        var sampler = new DatabaseActivitySampler(_ =>
        {
            sampled.TrySetResult();
            return Task.FromResult(rows);
        }, () => ValueTask.CompletedTask, CancellationToken.None);

        await sampled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sampler.StopAsync();
        var report = sampler.Report();
        var count = report["sampleCount"]!.GetValue<long>();

        Assert.True(count > 0);
        Assert.InRange(report["groups"]!.AsArray().Count, 1, 129);
        Assert.Equal(256L * count, report["blockedBackendObservations"]!.GetValue<long>());
        Assert.Equal(256L, report["maximumBlockedBackends"]!.GetValue<long>());
    }
}
