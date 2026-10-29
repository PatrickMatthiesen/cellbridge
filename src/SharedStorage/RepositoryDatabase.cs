using Microsoft.EntityFrameworkCore;
using System.Data;

namespace CellBridge.Storage.Shared;

internal interface IRepositoryDatabase<TContext> where TContext : DbContext
{
    TContext CreateContext();
    IsolationLevel ReadIsolation { get; }
    Task ConfigureAsync(TContext db, CancellationToken ct);
    Task CheckHealthAsync(TContext db, CancellationToken ct);
    Task GuardAsync(TContext db, Guid id, CancellationToken ct);
    Task RecoveryGuardAsync(TContext db, CancellationToken ct);
    IQueryable<ObjectRow> ObjectsAtKeys(TContext db, IReadOnlyList<string> keys);
    Task<DateTime> ClockAsync(TContext db, CancellationToken ct);
    bool IsDatabaseException(Exception error);
    bool IsUniqueViolation(Exception error);
}
