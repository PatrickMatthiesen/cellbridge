using Microsoft.EntityFrameworkCore.Storage;

namespace CellBridge.Lists.PostgreSql;

internal sealed class PostgreSqlListWorkspaceTransaction(
    PostgreSqlListDbContext context, IDbContextTransaction transaction) : IAsyncDisposable
{
    internal PostgreSqlListDbContext Context => context;

    internal async Task CommitAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try { await transaction.DisposeAsync(); }
        finally { await context.DisposeAsync(); }
    }
}
