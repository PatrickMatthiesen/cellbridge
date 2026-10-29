using System.Diagnostics.Metrics;
using CellBridge.Diagnostics;

[CollectionDefinition("CellBridge metrics", DisableParallelization = true)]
public sealed class CellBridgeMetricsCollection;

[Collection("CellBridge metrics")]
public sealed class CellBridgeMetricsTests
{
    [Fact]
    public void DisabledScopesAllocateNothingAfterInitialization()
    {
        for (var i = 0; i < 100; i++) CellBridgeMetrics.Measure(CellBridgePhase.SaveTotal).Dispose();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) CellBridgeMetrics.Measure(CellBridgePhase.SaveTotal).Dispose();
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void ListenerReceivesFixedPhaseTagsAndSecondsIncludingFailedScopes()
    {
        var samples = new List<(double Duration, string Phase)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name != CellBridgeMetrics.MeterName) return;
            Assert.Equal(CellBridgeMetrics.DurationName, instrument.Name);
            Assert.Equal("s", instrument.Unit);
            Assert.IsType<Histogram<double>>(instrument);
            current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            Assert.Equal(1, tags.Length);
            Assert.Equal("phase", tags[0].Key);
            samples.Add((value, Assert.IsType<string>(tags[0].Value)));
        });
        listener.Start();
        var phases = Enum.GetValues<CellBridgePhase>();
        foreach (var phase in phases)
            using (CellBridgeMetrics.Measure(phase)) { }
        Assert.Equal(phases.Length, samples.Count);
        Assert.Equal(phases.Length, samples.Select(sample => sample.Phase).Distinct().Count());
        Assert.All(samples, sample => Assert.True(double.IsFinite(sample.Duration) && sample.Duration >= 0));
        Assert.Contains(samples, sample => sample.Phase == "save.total");
        Assert.Contains(samples, sample => sample.Phase == "publication.connection_and_transaction");
        void Cancelled()
        {
            using var timing = CellBridgeMetrics.Measure(CellBridgePhase.ContentStaging);
            throw new OperationCanceledException();
        }
        Assert.Throws<OperationCanceledException>(Cancelled);
        Assert.Equal(phases.Length + 1, samples.Count);
        Assert.Equal("content.stage", samples[^1].Phase);
        listener.Dispose();
        CellBridgeMetrics.Measure(CellBridgePhase.SaveTotal).Dispose();
        Assert.Equal(phases.Length + 1, samples.Count);
    }
}
