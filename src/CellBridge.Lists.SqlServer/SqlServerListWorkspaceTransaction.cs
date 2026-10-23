using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CellBridge.Lists.SqlServer;

/// <summary>Owns workspace context on a dedicated connection for one operation.</summary>
internal sealed class SqlServerListWorkspaceTransaction : IAsyncDisposable
{
    internal SqlServerListDbContext Context { get; }
    private readonly IDbContextTransaction transaction;

    private SqlServerListWorkspaceTransaction(SqlServerListDbContext context, IDbContextTransaction transaction)
    {
        Context = context;
        this.transaction = transaction;
    }

    internal static async Task<SqlServerListWorkspaceTransaction> OpenAsync(
        DbContextOptions<SqlServerListDbContext> options, Guid workspaceId, CancellationToken ct = default)
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
            await context.Database.OpenConnectionAsync(ct);
            transaction = await context.Database.BeginTransactionAsync(ct);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                SET XACT_ABORT ON; SET LOCK_TIMEOUT 10000;
                DECLARE @workspace varbinary(16) = CONVERT(binary(16), {workspaceId});
                SET CONTEXT_INFO @workspace;
                """, ct);
            return new(context, transaction);
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            await ResetAndDisposeAsync(context);
            throw;
        }
    }

    internal async Task CommitAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try { await transaction.DisposeAsync(); }
        finally { await ResetAndDisposeAsync(Context); }
    }

    private static async Task ResetAndDisposeAsync(SqlServerListDbContext context)
    {
        try
        {
            if (context.Database.IsSqlServer() && context.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                await context.Database.ExecuteSqlRawAsync("SET CONTEXT_INFO 0x;", CancellationToken.None);
        }
        catch (SqlException)
        {
            // A broken connection must never return a previous workspace to the pool.
            // Cleanup failure does not reverse a known committed operation.
            SqlConnection.ClearPool((SqlConnection)context.Database.GetDbConnection());
        }
        finally { await context.DisposeAsync(); }
    }
}
