using BenchmarkDotNet.Running;

// The caller serializes collection and reserves the machine. No separate runtime driver.
Settings.Validate();
if (args.SequenceEqual(["--profile-mutations"]))
{
    await CommandDiagnostics.ProfileMutationsAsync();
    return;
}
if (args.SequenceEqual(["--diagnostics"]))
{
    await CommandDiagnostics.RunAsync();
    return;
}
if (args.SequenceEqual(["--smoke"]))
{
    // Exercise the corrected warm/reset lifecycle twice without collecting timings.
    foreach (var variant in Settings.Variants)
        foreach (var operation in new[] { "create", "patch" })
        {
            var benchmark = new ListMutationBenchmarks { Variant = variant, Operation = operation };
            try
            {
                await benchmark.Setup();
                for (var i = 0; i < 2; i++)
                { benchmark.Prepare(); await benchmark.Batch(); benchmark.Verify(); }
            }
            finally { await benchmark.Cleanup(); }
        }
    Console.WriteLine("Warm mutation setup, two schema resets, and result validation passed for each selected variant and operation.");
    return;
}
var summaries = BenchmarkSwitcher.FromTypes([
    typeof(PagingBenchmarks), typeof(ListMutationBenchmarks), typeof(ContentReadBenchmarks),
    typeof(ContentWriteBenchmarks), typeof(ServiceSaveBenchmarks)]).Run(args).ToArray();
if (!args.Contains("--list") && (summaries.Length == 0 || summaries.Any(s => s.HasCriticalValidationErrors ||
        s.Reports.Length == 0 || s.Reports.Any(r => !r.Success))))
    Environment.ExitCode = 1;
