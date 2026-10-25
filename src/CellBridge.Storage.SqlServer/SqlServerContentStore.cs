using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

/// <summary>Immutable SHA-256 content in bounded database chunks.</summary>
public sealed class SqlServerContentStore : IContentStore, IStorageBudgetParticipant
{
    private readonly DocumentContent<SqlServerDocumentDbContext> content;
    public SqlServerContentStore(DbContextOptions<SqlServerDocumentDbContext> options, long maxObjectBytes = 512L * 1024 * 1024)
        : this(options, maxObjectBytes, options) { }
    private SqlServerContentStore(DbContextOptions<SqlServerDocumentDbContext> options, long maxObjectBytes, object budgetScope)
    {
        options = SqlServerDocumentDatabase.Normalize(options);
        content = new(new SqlServerDocumentDatabase(options), maxObjectBytes, budgetScope);
    }
    public bool Durable => content.Durable;
    public bool Shared => content.Shared;
    public object BudgetScope => content.BudgetScope;
    public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => content.WriteAsync(source, cancellationToken);
    public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => content.OpenReadAsync(handle, cancellationToken);
}
