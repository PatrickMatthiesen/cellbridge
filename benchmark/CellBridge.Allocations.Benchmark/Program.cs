using BenchmarkDotNet.Running;

if (args.Contains("--diagnostics", StringComparer.Ordinal))
    await AllocationDiagnostics.RunAsync(args.Where(a => a != "--diagnostics").ToArray());
else
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
