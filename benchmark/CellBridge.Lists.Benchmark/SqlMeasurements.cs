using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

// Each Npgsql command or batch execution is a SQL execution round trip. Commit
// is reported separately. Statements inside a batch are not separate round trips.
internal sealed class SqlMeasurements : IDisposable
{
    private static readonly AsyncLocal<SqlMeasurements?> current = new();
    private readonly SqlMeasurements? previous;
    public int Executions;
    public int Commits;
    private SqlMeasurements() { previous = current.Value; current.Value = this; }
    public static SqlMeasurements Begin() => new();
    public void Dispose() => current.Value = previous;

    public static bool Command(NpgsqlCommand command)
    {
        if (current.Value is { } measurement)
        {
            Interlocked.Increment(ref measurement.Executions);
        }
        return true;
    }

    public static bool Batch(NpgsqlBatch batch)
    {
        if (current.Value is { } measurement) Interlocked.Increment(ref measurement.Executions);
        return true;
    }

    public static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    // CommitAsync uses Npgsql's internal protocol path, which bypasses command
    // tracing. Its completed-transaction event runs only after server success.
    public sealed class CommitLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CommitLogger(categoryName == "Npgsql.Transaction");
        public void Dispose() { }
        private sealed class CommitLogger(bool transactionCategory) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => transactionCategory && level == LogLevel.Debug;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (transactionCategory && eventId.Name == "CommittedTransaction" && current.Value is { } measurement)
                    Interlocked.Increment(ref measurement.Commits);
            }
        }
    }
}
