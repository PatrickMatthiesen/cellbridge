using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

public sealed record StorageMaintenanceReport(long StoredBytes, long Documents, long Snapshots,
    long ReferencedObjects, long OrphanObjects, long OrphanBytes, bool Applied);

/// <summary>
/// Quiescent maintenance only. All hosts, readers and writers must be stopped.
/// Every historic snapshot remains a root; this does not expire protocol history.
/// </summary>
public sealed class StorageMaintenance(NpgsqlDataSource source)
{
    /// <summary>Migration backfill for existing filesystem bytes, including unreferenced objects.</summary>
    public async Task RegisterExistingFileSystemAsync(string root, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*.blob"))
        {
            string key = Path.GetFileNameWithoutExtension(path);
            await using var input = File.OpenRead(path);
            if (key.Length != 64 || key != Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken)))
                throw new StorageCorruptionException("Filesystem inventory contains a corrupt object.");
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await PostgreSqlStateStore.ConfigureTransactionAsync(connection, transaction, cancellationToken);
            await using var insert = new NpgsqlCommand("INSERT INTO cellbridge_objects VALUES ($1,$2,$1) ON CONFLICT DO NOTHING", connection, transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = key });
            insert.Parameters.Add(new NpgsqlParameter { Value = input.Length });
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 0)
            {
                // Existing data can be over budget. Backfill records it without deleting it.
                await using var account = new NpgsqlCommand("UPDATE cellbridge_usage SET stored_bytes=stored_bytes+$1 WHERE singleton=true", connection, transaction);
                account.Parameters.Add(new NpgsqlParameter { Value = input.Length });
                await account.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var verify = new NpgsqlCommand("SELECT length,sha256 FROM cellbridge_objects WHERE object_key=$1", connection, transaction);
                verify.Parameters.Add(new NpgsqlParameter { Value = key });
                await using var metadata = await verify.ExecuteReaderAsync(cancellationToken);
                if (!await metadata.ReadAsync(cancellationToken) || metadata.GetInt64(0) != input.Length || metadata.GetString(1) != key)
                    throw new StorageCorruptionException("Filesystem inventory disagrees with reserved metadata.");
            }
            await transaction.CommitAsync(CancellationToken.None);
        }
    }

    public async Task<StorageMaintenanceReport> CollectOrphansAsync(bool apply = false,
        bool quiescent = false, string? fileSystemRoot = null, CancellationToken cancellationToken = default)
    {
        if (apply && !quiescent) throw new InvalidOperationException("Applying maintenance requires all readers and writers to be stopped.");
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgreSqlStateStore.ConfigureTransactionAsync(connection, transaction, cancellationToken);
        // Mutations take document/object locks before the ledger, matching normal publication.
        // LOCK TABLE excludes concurrent SQL publication during the mark/sweep transaction.
        await using (var gate = new NpgsqlCommand("LOCK TABLE cellbridge_documents,cellbridge_states,cellbridge_objects,cellbridge_chunks IN SHARE ROW EXCLUSIVE MODE", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        var references = new Dictionary<string, ContentHandle>(StringComparer.Ordinal);
        long snapshots = 0;
        await using (var states = new NpgsqlCommand("SELECT state_json FROM cellbridge_states", connection, transaction))
        await using (var reader = await states.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var state = JsonSerializer.Deserialize<DocumentState>(reader.GetString(0));
                if (state is null || state.FormatVersion != DocumentState.CurrentFormat)
                    throw new StorageCorruptionException("Unsupported state encountered during maintenance.");
                snapshots++;
                foreach (var handle in StorageReferences.Handles(state))
                {
                    if (references.TryGetValue(handle.Key, out var other) && handle != other)
                        throw new StorageCorruptionException("Conflicting metadata for a referenced content key.");
                    references[handle.Key] = handle;
                }
            }
        }
        var orphans = new List<ContentHandle>();
        await using (var objects = new NpgsqlCommand("SELECT object_key,length,sha256 FROM cellbridge_objects", connection, transaction))
        await using (var reader = await objects.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var handle = new ContentHandle(reader.GetString(0), reader.GetInt64(1), reader.GetString(2));
                if (!references.Remove(handle.Key, out var expected)) orphans.Add(handle);
                else if (expected != handle) throw new StorageCorruptionException("Referenced object metadata is corrupt.");
            }
        }
        if (references.Count != 0) throw new StorageCorruptionException("Referenced objects are absent from the usage inventory. Run filesystem migration backfill before collection.");
        long orphanBytes = orphans.Sum(h => h.Length);
        if (apply)
        {
            foreach (var orphan in orphans)
            {
                if (fileSystemRoot is not null)
                {
                    if (orphan.Key.Length != 64 || orphan.Key.Any(c => !char.IsAsciiHexDigit(c)))
                        throw new StorageCorruptionException("Invalid orphan key.");
                    // Unlink before releasing its charge. A crash leaves a repeatable charged reservation.
                    CellBridge.Storage.FileSystem.FileSystemContentStore.DeleteQuiescentObject(fileSystemRoot, orphan.Key);
                }
                foreach (var table in new[] { "cellbridge_chunks", "cellbridge_objects" })
                {
                    await using var delete = new NpgsqlCommand($"DELETE FROM {table} WHERE object_key=$1", connection, transaction);
                    delete.Parameters.Add(new NpgsqlParameter { Value = orphan.Key });
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            await PostgreSqlStorageBudget.AdjustAsync(connection, transaction, -orphanBytes, 0, cancellationToken);
        }
        long bytes, documents, objectCount;
        await using (var usage = new NpgsqlCommand("SELECT stored_bytes,document_count,(SELECT COUNT(*) FROM cellbridge_objects) FROM cellbridge_usage", connection, transaction))
        await using (var reader = await usage.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new StorageCorruptionException("Missing usage ledger.");
            bytes = reader.GetInt64(0); documents = reader.GetInt64(1); objectCount = reader.GetInt64(2);
        }
        await transaction.CommitAsync(CancellationToken.None);
        return new(bytes, documents, snapshots, objectCount - (apply ? 0 : orphans.Count), orphans.Count, orphanBytes, apply);
    }
}
