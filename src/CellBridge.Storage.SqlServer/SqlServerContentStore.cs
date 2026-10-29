using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

/// <summary>Immutable SHA-256 content in bounded database chunks.</summary>
public sealed class SqlServerContentStore : IContentStore, IContentBatchReader, IContentBatchWriter
{
    internal readonly DocumentContent<SqlServerDocumentDbContext> content;
    internal string DatabaseIdentity { get; }
    public SqlServerContentStore(DbContextOptions<SqlServerDocumentDbContext> options, long maxObjectBytes = 512L * 1024 * 1024)
    {
        options = SqlServerDocumentDatabase.Normalize(options);
        using var identityContext = new SqlServerDocumentDbContext(options);
        DatabaseIdentity = identityContext.Database.GetConnectionString() ?? "";
        content = new(new SqlServerDocumentDatabase(options), maxObjectBytes);
    }
    public bool Durable => content.Durable;
    public bool Shared => content.Shared;
    public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => content.WriteAsync(source, cancellationToken);
    public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => content.OpenReadAsync(handle, cancellationToken);
    public ValueTask<IReadOnlyDictionary<string, byte[]>> ReadManyAsync(IReadOnlyCollection<ContentHandle> handles,
        CancellationToken cancellationToken = default) => content.ReadManyAsync(handles, cancellationToken);
    public ValueTask<IReadOnlyList<ContentHandle>> WriteManyAsync(IReadOnlyList<ReadOnlyMemory<byte>> contents,
        CancellationToken cancellationToken = default) => content.WriteManyAsync(contents, cancellationToken);
}
