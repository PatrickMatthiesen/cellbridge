using BenchmarkDotNet.Running;

if (args.Contains("--history-scaling", StringComparer.Ordinal))
{
    await HistoryScalingDiagnostics.RunAsync(args.Where(a => a != "--history-scaling").ToArray());
    return 0;
}

if (args.Contains("--diagnostics", StringComparer.Ordinal))
{
    await AllocationDiagnostics.RunAsync(args.Where(a => a != "--diagnostics").ToArray());
    return 0;
}

var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
    .Run(args.Length == 0 ? ["--filter", "*"] : args).ToArray();
return args.Any(argument => argument is "--list" or "--help" or "-h") ? 0 :
    summaries.Length == 0 || summaries.Any(summary => summary.HasCriticalValidationErrors ||
        summary.Reports.Length == 0 || summary.Reports.Any(report => !report.Success)) ? 1 : 0;
