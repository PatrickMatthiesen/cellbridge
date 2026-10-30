using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Tests;

public static class NormalizedLegacyDatabase
{
    public static readonly RecoveryReceipt OldReceipt = new(Guid.Parse("09ce460d-2c56-474c-baff-c9249af521fd"), new string('b', 64), 5, 36);
    public static async Task SeedAsync(DbContext db, bool sqlServer, ImmutableArray<DocumentState> snapshots, IContentStore content, bool storeChunks = true)
    {
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        foreach (var handle in ContentStoreReader.UniqueHandles(snapshots.SelectMany(StorageReferences.Handles)))
        {
            await Execute(connection, sqlServer ? "INSERT INTO cellbridge_documents.objects ([Key],Length,Sha256) VALUES (@p0,@p1,@p2)" :
                "INSERT INTO cellbridge_objects VALUES (@p0,@p1,@p2)", handle.Key, handle.Length, handle.Sha256);
            var bytes = await content.ReadVerifiedAsync(handle);
            for (var offset = 0; storeChunks && offset < bytes.Length; offset += 65536)
                await Execute(connection, sqlServer ? "INSERT INTO cellbridge_documents.chunks ([Key],Ordinal,Bytes) VALUES (@p0,@p1,@p2)" :
                    "INSERT INTO cellbridge_chunks VALUES (@p0,@p1,@p2)", handle.Key, offset / 65536, bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset)).ToArray());
        }
        foreach (var state in snapshots)
        {
            var json = DocumentStateJson.Serialize(state);
            await Execute(connection, sqlServer ? "INSERT INTO cellbridge_documents.snapshots (ResourceId,StateVersion,Data) VALUES (@p0,@p1,@p2)" :
                "INSERT INTO cellbridge_states VALUES (@p0,@p1,@p2)", state.ResourceId, state.StateVersion, sqlServer ? Encoding.UTF8.GetBytes(json) : json);
        }
        var current = snapshots[^1];
        if (sqlServer)
        {
            var path = Encoding.BigEndianUnicode.GetBytes(current.PathKey);
            await Execute(connection, "INSERT INTO cellbridge_documents.documents (ResourceId,PathKey,PathHash,RetiredPathCount,StateVersion,IsDeleted) VALUES (@p0,@p1,@p2,@p3,@p4,@p5)",
                current.ResourceId, path, SHA256.HashData(path), current.RetiredPathKeys.Length, current.StateVersion, current.IsDeleted);
            for (var i = 0; i < current.RetiredPathKeys.Length; i++)
            {
                var retired = Encoding.BigEndianUnicode.GetBytes(current.RetiredPathKeys[i]);
                await Execute(connection, "INSERT INTO cellbridge_documents.reservations (ResourceId,Ordinal,PathKey,PathHash) VALUES (@p0,@p1,@p2,@p3)", current.ResourceId, i, retired, SHA256.HashData(retired));
            }
        }
        else await Execute(connection, "INSERT INTO cellbridge_documents VALUES (@p0,@p1,@p2,@p3)", current.ResourceId, current.PathKey, current.StateVersion, current.IsDeleted);
        var metadata = JsonSerializer.Serialize(DocumentReadModels.Metadata(current));
        await Execute(connection, sqlServer ? "INSERT INTO cellbridge_documents.metadata (ResourceId,StateVersion,LifecycleGeneration,RevisionCount,Data) VALUES (@p0,@p1,@p2,@p3,@p4)" :
            "INSERT INTO cellbridge_read_heads VALUES (@p0,@p1,@p2,@p3,@p4)", current.ResourceId, current.StateVersion, current.LifecycleGeneration, current.Revisions.Length,
            sqlServer ? Encoding.UTF8.GetBytes(metadata) : metadata);
        foreach (var revision in current.Revisions)
        {
            var json = JsonSerializer.Serialize(DocumentReadModels.Summary(revision));
            await Execute(connection, sqlServer ? "INSERT INTO cellbridge_documents.revisions (ResourceId,LifecycleGeneration,RevisionNumber,Data) VALUES (@p0,@p1,@p2,@p3)" :
                "INSERT INTO cellbridge_revision_summaries VALUES (@p0,@p1,@p2,@p3)", current.ResourceId, current.LifecycleGeneration, (decimal)revision.RevisionNumber, sqlServer ? Encoding.UTF8.GetBytes(json) : json);
        }
        var receipt = JsonSerializer.Serialize(OldReceipt);
        await Execute(connection, sqlServer ? "INSERT INTO cellbridge_documents.recovery_receipts (OperationId,Data) VALUES (@p0,@p1)" :
            "INSERT INTO cellbridge_recovery_receipts VALUES (@p0,@p1)", OldReceipt.OperationId, sqlServer ? Encoding.UTF8.GetBytes(receipt) : receipt);
    }
    public static async Task<string> LegacyRowsAsync(DbContext db, bool sqlServer)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sqlServer ? "SELECT Data FROM cellbridge_documents.snapshots ORDER BY StateVersion" : "SELECT state_json FROM cellbridge_states ORDER BY state_version";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(sqlServer ? Convert.ToBase64String((byte[])reader.GetValue(0)) : reader.GetString(0));
        return string.Join("\n", rows);
    }
    private static async Task Execute(DbConnection connection, string sql, params object[] values)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) { var parameter = command.CreateParameter(); parameter.ParameterName = "p" + i; parameter.Value = values[i]; command.Parameters.Add(parameter); }
        await command.ExecuteNonQueryAsync();
    }
    public sealed class Content : IContentStore
    {
        private readonly Dictionary<string, byte[]> values = new(StringComparer.Ordinal);
        public bool Durable => false;
        public bool Shared => false;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            using var output = new MemoryStream(); await source.CopyToAsync(output, cancellationToken); var bytes = output.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); values[hash] = bytes; return new(hash, bytes.Length, hash);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<Stream>(new MemoryStream(values[handle.Key], writable: false)); }
    }
}
