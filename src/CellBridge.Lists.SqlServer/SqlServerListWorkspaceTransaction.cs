using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CellBridge.Lists.SqlServer;

/// <summary>Owns workspace context on a dedicated connection for one operation.</summary>
internal sealed class SqlServerListWorkspaceTransaction : IAsyncDisposable
{
    internal SqlServerListDbContext Context { get; }
    private readonly IDbContextTransaction transaction;
    private bool committed;

    private SqlServerListWorkspaceTransaction(SqlServerListDbContext context, IDbContextTransaction transaction)
    {
        Context = context;
        this.transaction = transaction;
    }

    internal static async Task<SqlServerListWorkspaceTransaction> OpenAsync(
        DbContextOptions<SqlServerListDbContext> options, Guid workspaceId, CancellationToken ct = default,
        TimeSpan? statementTimeout = null)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("A workspace ID is required.", nameof(workspaceId));
        var context = new SqlServerListDbContext(options);
        IDbContextTransaction? transaction = null;
        try
        {
            if (!context.Database.IsSqlServer() || context.Database.CreateExecutionStrategy().RetriesOnFailure)
                throw new ArgumentException("A non-retrying SQL Server EF provider is required.", nameof(options));
            if (new SqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString).MultipleActiveResultSets)
                throw new ArgumentException("Workspace operations require MultipleActiveResultSets=false.", nameof(options));
            context.Database.AutoSavepointsEnabled = false;
            var timeout = statementTimeout ?? TimeSpan.FromSeconds(10);
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(statementTimeout));
            // A consumer's shorter command timeout remains an upper bound.
            var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
            var configured = context.Database.GetCommandTimeout();
            if (configured is null or 0 || configured > seconds)
                context.Database.SetCommandTimeout(seconds);
            await context.Database.OpenConnectionAsync(ct);
            transaction = await context.Database.BeginTransactionAsync(ct);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                SET XACT_ABORT ON;
                SET LOCK_TIMEOUT {(int)Math.Ceiling(timeout.TotalMilliseconds)};
                DECLARE @workspace varbinary(16) = CONVERT(binary(16), {workspaceId});
                SET CONTEXT_INFO @workspace;
                """, ct);
            return new(context, transaction);
        }
        catch
        {
            try
            {
                if (transaction is not null) await transaction.DisposeAsync();
            }
            finally { await ResetAndDisposeAsync(context); }
            throw;
        }
    }

    internal async Task CommitAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try { await transaction.DisposeAsync(); }
        catch (Exception) when (committed)
        {
            await DiscardSessionAsync((SqlConnection)Context.Database.GetDbConnection());
        }
        finally { await ResetAndDisposeAsync(Context); }
    }

    private static async Task ResetAndDisposeAsync(SqlServerListDbContext context)
    {
        var connection = context.Database.IsSqlServer() ? context.Database.GetDbConnection() as SqlConnection : null;
        try
        {
            if (connection?.State == System.Data.ConnectionState.Open)
                await context.Database.ExecuteSqlRawAsync("SET CONTEXT_INFO 0x;", CancellationToken.None);
        }
        catch (Exception)
        {
            // ClearPool alone only marks active sessions for disposal on close.
            // EF leaves externally owned open connections open, so close explicitly
            // whenever reset fails, including failures raised by interceptors.
            if (connection is not null) await DiscardSessionAsync(connection);
        }
        finally
        {
            try { await context.DisposeAsync(); }
            catch (Exception)
            {
                if (connection is not null) await DiscardSessionAsync(connection);
            }
        }
    }

    private static async Task DiscardSessionAsync(SqlConnection connection)
    {
        // Cleanup must neither retain workspace authority nor report a known
        // committed mutation as failed. Discard the physical session before reuse.
        try { SqlConnection.ClearPool(connection); }
        catch (Exception) { /* Closing still revokes this connection's session authority. */ }
        try { await connection.CloseAsync(); }
        catch (Exception)
        {
            try { await connection.DisposeAsync(); }
            catch (Exception)
            {
                try { connection.Dispose(); }
                catch (Exception) { /* All discard paths were attempted; preserve the operation outcome. */ }
            }
        }
    }
}
