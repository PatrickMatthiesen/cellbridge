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
    Task<RepositoryControlRow[]> ReadControlRowsAsync(TContext db, Guid id, long lifecycle, bool includePartitions, bool includeClock, CancellationToken ct);
    Task<DateTime> ClockAsync(TContext db, CancellationToken ct);
    bool IsDatabaseException(Exception error);
    bool IsUniqueViolation(Exception error);
}

// Tagged rows keep the result linear in editors and partitions, without joins.
internal sealed class RepositoryControlRow
{
    public int Kind { get; set; }
    public string? Json { get; set; }
    public byte[]? EncodedJson { get; set; }
    public Guid? ClientId { get; set; }
    public int? Partition { get; set; }
    public long? Sequence { get; set; }
    public DateTime? UtcNow { get; set; }
}
