using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;

internal sealed partial class CommandDiagnostics
{
    private static string? profileSql;

    // Instrumented runs are deliberately separate from the unchanged BDN bodies.
    public static async Task ProfileMutationsAsync()
    {
        Enabled = true;
        using var observer = new CommandDiagnostics();
        foreach (var variant in Settings.Variants)
        foreach (var operation in new[] { "create", "patch" })
        {
            var benchmark = new ListMutationBenchmarks { Variant = variant, Operation = operation };
            var durations = new Dictionary<string, List<double>>();
            var observing = false;
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (!observing) return;
                    var sql = profileSql ?? activity.DisplayName;
                    if (!durations.TryGetValue(sql, out var values)) durations[sql] = values = [];
                    values.Add(activity.Duration.TotalMilliseconds);
                }
            };
            ActivitySource.AddActivityListener(listener);
            try
            {
                await benchmark.Setup();
                // Warm normal JIT/PGO before starting the allocation/CPU trace.
                for (var i = 0; i < 8; i++)
                { benchmark.Prepare(); await benchmark.Batch(); benchmark.Verify(); }
                var trace = Path.Combine(Settings.Output, $"{variant}-{operation}.nettrace");
                using var session = new DiagnosticsClient(Environment.ProcessId).StartEventPipeSession([
                    new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose,
                        (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Stack)),
                    new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
                    new EventPipeProvider("CellBridge-MutationProfile", EventLevel.Informational)
                ], requestRundown: true);
                await using var output = File.Create(trace);
                var copying = session.EventStream.CopyToAsync(output);
                var compilationStart = observer.compilations;
                try
                {
                    for (var i = 0; i < 48; i++)
                    {
                        benchmark.Prepare();
                        MutationProfileEvents.Log.BatchStart(operation, i);
                        observing = true;
                        try { await benchmark.Batch(); }
                        finally { observing = false; MutationProfileEvents.Log.BatchStop(); }
                        benchmark.Verify();
                    }
                }
                finally { session.Stop(); await copying; }
                File.WriteAllText(Path.Combine(Settings.Output, $"{variant}-{operation}-commands.json"),
                    JsonSerializer.Serialize(new { operation, batches = 48, operations = 48 * ListMutationBenchmarks.BatchSize,
                        note = "Instrumented command durations include driver and server work. Allocation/CPU analysis must select BatchStart/BatchStop regions. Setup and validation are outside those regions. Not benchmark timing.",
                        compilations = observer.compilations - compilationStart,
                        commands = durations.Select(pair => new { sql = pair.Key, count = pair.Value.Count,
                            sumMs = pair.Value.Sum(), medianMs = pair.Value.Order().ElementAt(pair.Value.Count / 2),
                            samplesMs = pair.Value }).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { await benchmark.Cleanup(); }
        }
    }
}

[EventSource(Name = "CellBridge-MutationProfile")]
internal sealed class MutationProfileEvents : EventSource
{
    public static readonly MutationProfileEvents Log = new();
    [Event(1)] public void BatchStart(string operation, int iteration) => WriteEvent(1, operation, iteration);
    [Event(2)] public void BatchStop() => WriteEvent(2);
}
