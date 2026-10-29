using CellBridge.Storage.Abstractions;
using Npgsql;

// Aspire owns the database. Reset document data between measurements so repeat
// input bytes do not turn content writes into deduplication lookups.
internal sealed class BenchmarkDatabase(NpgsqlDataSource source) : IAsyncDisposable
{
    public NpgsqlDataSource Source { get; } = source;

    public static async Task<BenchmarkDatabase> OpenAsync(string connectionString, StorageLimits limits)
    {
        var source = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var reset = source.CreateCommand("""
                TRUNCATE cellbridge_documents, cellbridge_states, cellbridge_read_heads,
                    cellbridge_revision_summaries, cellbridge_chunks, cellbridge_objects,
                    cellbridge_recovery_receipts;
                """))
                await reset.ExecuteNonQueryAsync();
            return new(source);
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => Source.DisposeAsync();
}
