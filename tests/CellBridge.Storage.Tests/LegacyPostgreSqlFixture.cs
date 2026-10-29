using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.Tests;

/// <summary>Seeds actual pre-EF schema rows for explicit migration tests.</summary>
internal static class LegacyPostgreSqlFixture
{
    internal static async Task SeedAsync(NpgsqlDataSource source, IContentStore content,
        IReadOnlyList<DocumentState> snapshots, int version, bool expandedJson)
    {
        await using (var stream = typeof(LegacyPostgreSqlFixture).Assembly.GetManifestResourceStream(
            "CellBridge.Storage.Tests.Fixtures.PostgreSqlSchema6.sql")!)
        using (var reader = new StreamReader(stream))
        await using (var schema = source.CreateCommand(await reader.ReadToEndAsync()))
            await schema.ExecuteNonQueryAsync();
        await using (var schema = source.CreateCommand("UPDATE cellbridge_schema SET version=$1"))
        {
            schema.Parameters.AddWithValue(version);
            await schema.ExecuteNonQueryAsync();
        }
        foreach (var handle in ContentStoreReader.UniqueHandles(snapshots.SelectMany(StorageReferences.Handles)))
        {
            await using var metadata = source.CreateCommand("INSERT INTO cellbridge_objects VALUES($1,$2,$3)");
            metadata.Parameters.AddWithValue(handle.Key);
            metadata.Parameters.AddWithValue(handle.Length);
            metadata.Parameters.AddWithValue(handle.Sha256);
            await metadata.ExecuteNonQueryAsync();
            var bytes = await content.ReadVerifiedAsync(handle);
            for (var offset = 0; offset < bytes.Length; offset += 65536)
            {
                await using var chunk = source.CreateCommand("INSERT INTO cellbridge_chunks VALUES($1,$2,$3)");
                chunk.Parameters.AddWithValue(handle.Key);
                chunk.Parameters.AddWithValue(offset / 65536);
                chunk.Parameters.AddWithValue(bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset)).ToArray());
                await chunk.ExecuteNonQueryAsync();
            }
        }
        foreach (var snapshot in snapshots)
        {
            await using var insert = source.CreateCommand("INSERT INTO cellbridge_states VALUES($1,$2,$3)");
            insert.Parameters.AddWithValue(snapshot.ResourceId);
            insert.Parameters.AddWithValue(snapshot.StateVersion);
            insert.Parameters.AddWithValue(expandedJson ? JsonSerializer.Serialize(snapshot) : DocumentStateJson.Serialize(snapshot));
            await insert.ExecuteNonQueryAsync();
        }
        foreach (var head in snapshots.GroupBy(snapshot => snapshot.ResourceId).Select(group => group.MaxBy(snapshot => snapshot.StateVersion)!))
        {
            await using var insert = source.CreateCommand("INSERT INTO cellbridge_documents VALUES($1,$2,$3,$4)");
            insert.Parameters.AddWithValue(head.ResourceId);
            insert.Parameters.AddWithValue(head.PathKey);
            insert.Parameters.AddWithValue(head.StateVersion);
            insert.Parameters.AddWithValue(head.IsDeleted);
            await insert.ExecuteNonQueryAsync();
        }
    }
}
