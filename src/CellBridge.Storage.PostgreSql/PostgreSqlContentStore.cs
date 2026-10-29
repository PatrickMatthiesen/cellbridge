using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.PostgreSql;

/// <summary>Immutable SHA-256 content in bounded database chunks.</summary>
public sealed class PostgreSqlContentStore : IContentStore, IContentBatchReader, IContentBatchWriter
{
    private readonly DocumentContent<CellBridgeDbContext> content;
    public PostgreSqlContentStore(Npgsql.NpgsqlDataSource dataSource, long maxObjectBytes = 512L * 1024 * 1024)
        : this(PostgreSqlDatabase.CreateOptions(dataSource), maxObjectBytes) { }
    public PostgreSqlContentStore(DbContextOptions<CellBridgeDbContext> options, long maxObjectBytes = 512L * 1024 * 1024)
    {
        options = PostgreSqlDatabase.NormalizeOptions(options);
        content = new(new PostgreSqlDocumentDatabase(options), maxObjectBytes);
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
