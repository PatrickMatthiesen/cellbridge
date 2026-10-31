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

// Providers that can return the normalized head and control records from one
// statement implement this optional interface. Other providers keep the
// existing two-query repository path.
internal interface IRepositoryControlSnapshotDatabase<TContext> where TContext : DbContext
{
    Task<RepositoryControlSnapshot?> ReadControlSnapshotAsync(TContext db, Guid id, CancellationToken ct);
}

internal sealed record RepositoryControlSnapshot(RepositoryHeadRow Head, RepositoryControlRow[] Rows);

// Provider query projection for a coherent control snapshot.
internal sealed class RepositoryControlSnapshotRow
{
    public required string HeadJson { get; set; }
    public required string RowsJson { get; set; }
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
