using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

internal sealed partial class PostgreSqlDocumentDatabase
{
    public async IAsyncEnumerable<(DocumentRow Pointer, string? Json)> ReadNamespaceRowsAsync(CellBridgeDbContext db,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // json_object_agg keeps duplicate properties so the common parser can
        // reject ambiguous identity fields. History and graphs stay on the server.
        var rows = db.Database.SqlQueryRaw<NamespaceRow>("""
            SELECT d.resource_id AS "ResourceId", d.path_key AS "PathKey",
                d.state_version AS "StateVersion", d.is_deleted AS "IsDeleted",
                (SELECT json_object_agg(field.key,field.value)::text
                    FROM json_each(s.state_json::json) field
                    WHERE field.key COLLATE "C" IN ('FormatVersion','ResourceId','StateVersion','PathKey',
                        'IsDeleted','LifecycleGeneration','ReplacedBy','RetiredPathKeys')) AS "NamespaceJson"
            FROM cellbridge_documents d LEFT JOIN cellbridge_states s
                ON d.resource_id=s.resource_id AND d.state_version=s.state_version
            """).AsAsyncEnumerable();
        await using var iterator = rows.GetAsyncEnumerator(ct);
        while (await MoveNextAsync(iterator))
        {
            var row = iterator.Current;
            yield return (new DocumentRow { ResourceId = row.ResourceId, PathKey = row.PathKey,
                StateVersion = row.StateVersion, IsDeleted = row.IsDeleted }, row.NamespaceJson);
        }
    }

    private static async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<NamespaceRow> rows)
    {
        try { return await rows.MoveNextAsync(); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InvalidTextRepresentation or PostgresErrorCodes.InvalidParameterValue)
        { throw new StorageCorruptionException("Invalid persisted namespace JSON.", ex); }
    }

    private sealed class NamespaceRow
    {
        public Guid ResourceId { get; set; }
        public required string PathKey { get; set; }
        public long StateVersion { get; set; }
        public bool IsDeleted { get; set; }
        public string? NamespaceJson { get; set; }
    }
}
