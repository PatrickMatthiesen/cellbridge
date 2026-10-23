using System.Diagnostics;
using CellBridge.Lists;
using Npgsql;

// Explicitly excluded from latency/allocation results. Uses Npgsql 10's ActivitySource
// and tracing filters, the same supported hooks as frozen SqlMeasurements.
internal sealed class CommandDiagnostics : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    public static bool Enabled { get; private set; }
    private static readonly Dictionary<string, int> commands = new();
    private static bool capture;
    private int compilations;
    private readonly List<IDisposable> subscriptions = [];
    public CommandDiagnostics() => subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
    public static bool Command(NpgsqlCommand command)
    { if (capture) { lock (commands) commands[command.CommandText] = commands.GetValueOrDefault(command.CommandText) + 1; } return true; }
    public static bool Batch(NpgsqlBatch batch)
    { if (capture) { var sql = string.Join(";\n", batch.BatchCommands.Cast<NpgsqlBatchCommand>().Select(c => c.CommandText)); lock (commands) commands[sql] = commands.GetValueOrDefault(sql) + 1; } return true; }
    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.EntityFrameworkCore") subscriptions.Add(listener.Subscribe(this,
            name => name.EndsWith("QueryCompilationStarting", StringComparison.Ordinal)));
    }
    public void OnNext(KeyValuePair<string, object?> value) { if (value.Key.EndsWith("QueryCompilationStarting", StringComparison.Ordinal)) Interlocked.Increment(ref compilations); }
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose() { foreach (var item in subscriptions) item.Dispose(); }

    public static async Task RunAsync()
    {
        Enabled = true;
        using var observer = new CommandDiagnostics();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        foreach (var variant in Settings.Variants)
        {
            var evidence = new Evidence("untimed-command-diagnostics", variant, new {
                note = "Command/batch executions, not statements, packets or commits. EF compilations count QueryCompilationStarting. Never use this mode for timing.",
                warming = "Ten first/second-page queries after normal preparation; Lists mutations retain the GlobalSetup-warmed engine across schema resets and the existing throwaway batch before observation; other workloads unchanged." });
            async Task Observe(string operation, int operations, Func<Task> work)
            {
                commands.Clear(); var before = observer.compilations;
                capture = true;
                try { await work(); }
                finally { capture = false; }
                evidence.Write("observed", new { operation, operations, commandOrBatchExecutions = commands.Values.Sum(),
                    efQueryCompilations = observer.compilations - before,
                    commands = commands.Select(p => new { sql = p.Key, executions = p.Value }).ToArray() });
            }
            foreach (var analyzed in new[] { false, true })
            {
                var paging = new PagingBenchmarks { Variant = variant, Analyzed = analyzed };
                try
                {
                    await paging.Setup();
                    for (var i = 0; i < 10; i++) { await paging.FirstPage(); await paging.SecondPage(); }
                    await Observe($"first-page-analyzed-{analyzed}", 1, async () => Inputs.VerifyPage(await paging.FirstPage(), 500));
                    await Observe($"second-page-analyzed-{analyzed}", 1, async () => Inputs.VerifyPage(await paging.SecondPage(), 600));
                    for (var repeat = 0; repeat < 2; repeat++)
                        await Observe($"forty-filter-values-analyzed-{analyzed}-pass-{repeat + 1}", 40, async () =>
                        {
                            for (var value = 200; value < 240; value++)
                            {
                                var page = await paging.QueryThreshold(value);
                                Inputs.VerifyPage(page, value);
                            }
                        });
                }
                finally { await paging.Cleanup(); }
            }
            foreach (var operation in new[] { "create", "patch" })
            {
                var mutations = new ListMutationBenchmarks { Variant = variant, Operation = operation };
                try
                {
                    await mutations.Setup(); mutations.Prepare(); await mutations.Batch(); mutations.Verify();
                    mutations.Prepare();
                    await Observe(operation, ListMutationBenchmarks.BatchSize, mutations.Batch);
                    mutations.Verify();
                }
                finally { await mutations.Cleanup(); }
            }
            var writes = new ContentWriteBenchmarks { Variant = variant };
            try
            {
                await writes.Setup(); writes.Prepare(); await writes.Batch(); writes.Verify();
                writes.Prepare(); await Observe("content-write", ContentWriteBenchmarks.BatchSize, writes.Batch); writes.Verify();
            }
            finally { await writes.Cleanup(); }
            var reads = new ContentReadBenchmarks { Variant = variant };
            try
            {
                await reads.Setup(); await reads.Read(); await Observe("content-read", 1, reads.Read);
            }
            finally { await reads.Cleanup(); }
            foreach (var mode in new[] { "changed-full", "delta" })
            {
                var saves = new ServiceSaveBenchmarks { Variant = variant, Mode = mode };
                try
                {
                    await saves.Setup(); saves.Prepare(); await saves.Batch(); saves.Verify();
                    saves.Prepare(); await Observe("service-save-" + mode, ServiceSaveBenchmarks.BatchSize, saves.Batch); saves.Verify();
                }
                finally { await saves.Cleanup(); }
            }
            evidence.Write("validated", new { allObservedOperationsValidated = true });
        }
    }
}
