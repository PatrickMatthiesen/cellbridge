using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CellBridge.Diagnostics;

/// <summary>Fixed phases for opt-in operation timing. Durations include failed and cancelled attempts.</summary>
public enum CellBridgePhase
{
    SaveTotal, CoherentPrepare, GraphPreparation, GraphResolve, PackageMaterialization,
    ContentStaging, ProviderPayloadValidation, GuardedPublication,
    PublicationConnectionAndTransaction, DocumentGuard, PublicationWork, PublicationSaveChanges, PublicationCommit,
    CoherentReadConnectionAndTransaction, ImmutableReadConnection,
    ContentBatch, ContentWriterWait, ContentHashing, ContentConnectionAndTransaction, ContentWrite, ContentSaveChanges, ContentCommit,
}

/// <summary>Enable the meter with a .NET metrics listener or dotnet-counters. No listener means no timestamps or per-call allocations.</summary>
public static class CellBridgeMetrics
{
    public const string MeterName = "CellBridge.Operations";
    public const string DurationName = "cellbridge.operation.duration";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(DurationName, "s",
        "Elapsed phase duration, including waits and unsuccessful attempts. Nested phases overlap.");
    private static readonly string[] PhaseNames =
    [
        "save.total", "save.coherent_prepare", "graph.prepare", "graph.resolve", "package.materialize_validate",
        "content.stage", "publication.validate_payloads", "publication.guarded_total",
        "publication.connection_and_transaction", "publication.document_guard", "publication.work", "publication.save_changes", "publication.commit",
        "read.coherent_connection_and_transaction", "read.immutable_connection",
        "content.batch", "content.writer_wait", "content.hash", "content.connection_and_transaction", "content.write", "content.save_changes", "content.commit",
    ];

    public static Timing Measure(CellBridgePhase phase) => Duration.Enabled
        ? new(PhaseNames[(int)phase]) : default;

    public readonly struct Timing : IDisposable
    {
        private readonly string? phase;
        private readonly long started;
        internal Timing(string phase)
        {
            this.phase = phase;
            started = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if (phase is not null)
                Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("phase", phase));
        }
    }
}
