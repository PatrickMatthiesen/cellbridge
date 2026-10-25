using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.PostgreSql;

/// <summary>Immutable SHA-256 content in bounded database chunks.</summary>
public sealed class PostgreSqlContentStore : IContentStore, IStorageBudgetParticipant
{
    private readonly DocumentContent<CellBridgeDbContext> content;
    public PostgreSqlContentStore(Npgsql.NpgsqlDataSource dataSource, long maxObjectBytes = 512L * 1024 * 1024)
        : this(PostgreSqlDatabase.CreateOptions(dataSource), maxObjectBytes, dataSource) { }
    public PostgreSqlContentStore(DbContextOptions<CellBridgeDbContext> options, long maxObjectBytes = 512L * 1024 * 1024)
        : this(options, maxObjectBytes, options) { }
    private PostgreSqlContentStore(DbContextOptions<CellBridgeDbContext> options, long maxObjectBytes, object budgetScope)
    {
        options = PostgreSqlDatabase.NormalizeOptions(options);
        content = new(new PostgreSqlDocumentDatabase(options), maxObjectBytes, budgetScope);
    }
    public bool Durable => content.Durable;
    public bool Shared => content.Shared;
    public object BudgetScope => content.BudgetScope;
    public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => content.WriteAsync(source, cancellationToken);
    public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => content.OpenReadAsync(handle, cancellationToken);
}
