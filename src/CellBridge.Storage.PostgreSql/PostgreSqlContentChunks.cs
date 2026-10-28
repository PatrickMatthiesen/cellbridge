using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace CellBridge.Storage.PostgreSql;

internal sealed partial class PostgreSqlDocumentDatabase
{
    public async Task<bool> TryInsertChunksAsync(CellBridgeDbContext db, IReadOnlyList<ChunkRow> chunks,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var core = options.FindExtension<CoreOptionsExtension>();
        // Consumer models, EF services and SaveChanges hooks retain the tracked
        // write path. Resolve hooks through EF to include injected interceptors.
        if (core is { Model: not null } or { InternalServiceProvider: not null } or { ReplacedServices.Count: > 0 }
            || db.GetService<IInterceptors>().Aggregate<ISaveChangesInterceptor>() is not null
            || db.ChangeTracker.HasChanges())
            return false;
        if (chunks.Count == 0) return true;

        var keys = new string[chunks.Count];
        var ordinals = new int[chunks.Count];
        var payloads = new byte[chunks.Count][];
        for (var index = 0; index < chunks.Count; index++)
        {
            keys[index] = chunks[index].ObjectKey;
            ordinals[index] = chunks[index].Ordinal;
            payloads[index] = chunks[index].Bytes;
        }
        // Stable SQL and three typed parameters serve every bounded batch size.
        // EF keeps the current transaction, timeout and command interceptors.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO cellbridge_chunks (object_key, ordinal, bytes)
            SELECT * FROM unnest(@keys::text[], @ordinals::integer[], @payloads::bytea[])
            """, new object[]
            {
                new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = keys },
                new NpgsqlParameter("ordinals", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = ordinals },
                new NpgsqlParameter("payloads", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = payloads },
            }, ct);
        return true;
    }
}
