using CellBridge.TestInfrastructure;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

internal static class CellBridgeLoadTests
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine("--load-test --backend postgresql|sqlserver --config <NBomber JSON> --target <scenario names>");
            Console.WriteLine("Scenarios: document-memory, document-postgresql, document-filesystem, document-http, lists-provider, lists-http");
            Console.WriteLine("NBomber accepts --config/-c and --target/-t. Remaining arguments pass to NBomber unchanged.");
            return 0;
        }
        var backend = "postgresql";
        var backendIndex = Array.IndexOf(args, "--backend");
        if (backendIndex >= 0)
        {
            if (backendIndex + 1 >= args.Length) throw new ArgumentException("--backend requires postgresql or sqlserver.");
            backend = args[backendIndex + 1];
            args = args.Where((_, index) => index != backendIndex && index != backendIndex + 1).ToArray();
        }
        await using var resources = new LoadResources(backend);
        var documents = new[] { "memory", "postgresql", "filesystem", "http" }
            .Select(backend => new DocumentLoadScenario(backend, resources)).ToArray();
        var lists = new[] { false, true }.Select(http => new ListsLoadScenario(http, resources)).ToArray();
        var scenarios = documents.Select(document => document.Create())
            .Concat(lists.Select(list => list.Create())).ToArray();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var runner = NBomberRunner.RegisterScenarios(scenarios)
                .WithTestSuite("CellBridge")
                .WithTestName("API and provider load")
                .WithScenarioCompletionTimeout(TimeSpan.FromSeconds(30))
                .WithReportFolder(Path.Combine(RepositoryRoot(), "artifacts", "load-tests"));
            using var registration = cancellation.Token.Register(() => resources.Stop("Cancelled"));
            var result = await Task.Run(() => runner.Run(args));
            // These checks run outside NBomber's clean callbacks, which swallow exceptions.
            foreach (var document in documents) await document.VerifyAsync();
            foreach (var list in lists) await list.VerifyAsync();
            var failed = cancellation.IsCancellationRequested || resources.Failed ||
                result.ScenarioStats.Any(s => UnexpectedFailures(s.Fail) || s.StepStats.Any(step => UnexpectedFailures(step.Fail))) || result.ScenarioStats.Length == 0 ||
                result.ScenarioStats.Any(s => s.CurrentOperation != OperationType.Complete || s.Ok.Request.Count + s.Fail.Request.Count == 0) ||
                result.Thresholds.Any(t => t.IsFailed);
            return failed ? 1 : 0;
        }
        catch (Exception error)
        {
            // Provider exception text can include connection details.
            Console.Error.WriteLine($"Load test failed ({error.GetType().Name}).");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            foreach (var document in documents) await document.DisposeAsync();
            foreach (var list in lists) await list.DisposeAsync();
        }
    }

    private static bool UnexpectedFailures(MeasurementStats failed) => failed.Request.Count >
        failed.StatusCodes.Where(code => code.StatusCode is ListsLoadScenario.RevisionConflict or
            DocumentLoadScenario.ConflictStatus or DocumentLoadScenario.ReplayConflictStatus).Sum(code => code.Count);

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CellBridge.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run the benchmark from its repository checkout.");
    }

    internal static ScenarioProps Defaults(ScenarioProps scenario) => scenario
        .WithWarmUpDuration(TimeSpan.FromSeconds(30))
        .WithLoadSimulations(Simulation.KeepConstant(copies: 8, during: TimeSpan.FromMinutes(2)))
        .WithRestartIterationOnFail(false)
        .WithStatsBasedOnIterations(true);

    internal static async Task<T> StepAsync<T>(string name, IScenarioContext context, Func<Task<T>> action)
    {
        var response = await Step.Run(name, context, async () => Response.Ok(await action()));
        if (response.IsError || response.Payload is null)
            throw new InvalidOperationException($"Load step {name} failed.");
        return response.Payload.Value;
    }
}

internal sealed class LoadResources : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Lazy<Task<LoadTestEnvironment>> environment;
    private IScenarioContext? current;
    private int failed;
    public LoadResources(string backend) => environment = new(() => LoadTestEnvironment.StartAsync(cancellation.Token, backend));
    public CancellationToken CancellationToken => cancellation.Token;
    public bool Failed => Volatile.Read(ref failed) != 0;
    public Task<LoadTestEnvironment> EnvironmentAsync() => environment.Value;
    public async Task InitializeAsync(Func<Task> initialize)
    {
        try { await initialize(); }
        catch
        {
            Stop("Scenario initialization failed");
            throw;
        }
    }
    public void Observe(IScenarioContext context) => Interlocked.Exchange(ref current, context);
    public void Stop(string reason)
    {
        Interlocked.Exchange(ref failed, 1);
        cancellation.Cancel();
        Volatile.Read(ref current)?.StopCurrentTest(reason);
    }
    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        try
        {
            if (environment.IsValueCreated)
            {
                LoadTestEnvironment initialized;
                try { initialized = await environment.Value; }
                catch { return; } // StartAsync disposes partial application resources before throwing.
                await initialized.DisposeAsync();
            }
        }
        finally { cancellation.Dispose(); }
    }
}
