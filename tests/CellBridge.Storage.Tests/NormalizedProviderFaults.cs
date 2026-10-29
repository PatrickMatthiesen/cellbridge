using System.Collections.Concurrent;
using System.Data.Common;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.Tests;

/// <summary>Test-only interception of actual normalized-head SQL, after its affected-row guard.</summary>
public sealed class NormalizedHeadPause(Guid resourceId) : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<DbTransaction, byte> transactions = new(ReferenceEqualityComparer.Instance);
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Pause { get; init; } = true;
    public bool Contains(DbTransaction transaction) => transactions.ContainsKey(transaction);
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (command.Transaction is { } transaction && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) &&
            command.CommandText.Contains("repository_heads", StringComparison.OrdinalIgnoreCase) &&
            command.CommandText.Contains("StateVersion", StringComparison.OrdinalIgnoreCase) &&
            command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == resourceId))
        {
            transactions.TryAdd(transaction, 0); Entered.TrySetResult();
            if (Pause)
            {
                try { await Continue.Task.WaitAsync(cancellationToken); }
                catch { await result.DisposeAsync(); throw; }
            }
        }
        return result;
    }
}
public sealed class NormalizedLostCommitReply(NormalizedHeadPause commands) : DbTransactionInterceptor
{
    private int thrown;
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (commands.Contains(transaction) && Interlocked.Exchange(ref thrown, 1) == 0)
            throw new StorageUnavailableException("Test transport lost the reply after the real normalized publication committed.", new IOException("Lost acknowledgement."));
        return Task.CompletedTask;
    }
}
public sealed class NormalizedChunkCancellation(CancellationTokenSource cancellation) : DbCommandInterceptor
{
    private int cancelled;
    private void After(DbCommand command)
    {
        if (command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase) &&
            command.CommandText.Contains("chunks", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref cancelled, 1) == 0)
            cancellation.Cancel();
    }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    { After(command); return ValueTask.FromResult(result); }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    { After(command); return ValueTask.FromResult(result); }
}
