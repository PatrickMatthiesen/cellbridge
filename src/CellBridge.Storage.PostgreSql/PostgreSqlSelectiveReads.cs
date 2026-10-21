using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

public sealed partial class PostgreSqlStateStore
{
    private const int ReadHeadColumnCount = 11;
    private const string ReadHeadColumns = "d.resource_id,d.path_key,d.state_version,d.is_deleted,h.state_version,h.lifecycle_generation,h.revision_count,h.metadata_json,a.total,a.current_count,a.latest";
    private const string ReadHeadJoins = """
        LEFT JOIN cellbridge_read_heads h ON h.resource_id=d.resource_id AND h.state_version=d.state_version
        LEFT JOIN LATERAL (
            SELECT count(*) AS total,
                   count(*) FILTER (WHERE r.lifecycle_generation=h.lifecycle_generation) AS current_count,
                   max(r.revision_number) FILTER (WHERE r.lifecycle_generation=h.lifecycle_generation) AS latest
            FROM cellbridge_revision_summaries r WHERE r.resource_id=d.resource_id
        ) a ON true
        """;

    public ValueTask<DocumentReadMetadata?> FindReadMetadataByResourceIdAsync(Guid resourceId,
        CancellationToken cancellationToken = default) =>
        FindReadMetadataAsync("d.resource_id=$1", resourceId, cancellationToken);

    public ValueTask<DocumentReadMetadata?> FindReadMetadataByPathKeyAsync(string pathKey,
        CancellationToken cancellationToken = default) =>
        FindReadMetadataAsync("d.path_key=$1", pathKey, cancellationToken);

    private async ValueTask<DocumentReadMetadata?> FindReadMetadataAsync(string predicate, object value,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = dataSource.CreateCommand($"SELECT {ReadHeadColumns} FROM cellbridge_documents d {ReadHeadJoins} WHERE {predicate} AND NOT d.is_deleted");
            command.Parameters.Add(new NpgsqlParameter { Value = value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var metadata = DecodeReadMetadata(reader);
            if (await reader.ReadAsync(cancellationToken)) throw new StorageCorruptionException("Multiple current metadata projections.");
            return metadata;
        }
        catch (NpgsqlException ex) { throw new StorageUnavailableException("Document metadata could not be read.", ex); }
    }

    public async ValueTask<DocumentRevisionReadPage?> ReadRevisionSummariesAsync(Guid resourceId, int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        DocumentReadModels.ValidatePage(offset, limit);
        try
        {
            // The pointer, current authority fence and page are one MVCC statement.
            // LEFT joins distinguish a missing document from a corrupt projection.
            await using var command = dataSource.CreateCommand($"""
                SELECT {ReadHeadColumns},p.revision_number,p.summary_json
                FROM cellbridge_documents d {ReadHeadJoins}
                LEFT JOIN LATERAL (
                    SELECT r.revision_number,r.summary_json FROM cellbridge_revision_summaries r
                    WHERE r.resource_id=d.resource_id AND r.lifecycle_generation=h.lifecycle_generation
                    ORDER BY r.revision_number DESC OFFSET $2 LIMIT $3
                ) p ON true
                WHERE d.resource_id=$1 AND NOT d.is_deleted
                ORDER BY p.revision_number DESC
                """);
            command.Parameters.AddWithValue(resourceId);
            command.Parameters.AddWithValue(offset);
            command.Parameters.AddWithValue(limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var metadata = DecodeReadMetadata(reader);
            var count = reader.GetInt32(6);
            var revisions = ImmutableArray.CreateBuilder<DocumentRevisionSummary>();
            do
            {
                if (!reader.IsDBNull(ReadHeadColumnCount))
                    revisions.Add(DecodeRevisionSummary(reader, metadata));
            } while (await reader.ReadAsync(cancellationToken));
            var expected = Math.Min(limit, Math.Max(0, count - offset));
            if (revisions.Count != expected) throw new StorageCorruptionException("Revision projection page is incomplete.");
            var next = (long)offset + revisions.Count;
            return new(metadata, revisions.ToImmutable(), next < count ? checked((int)next) : null);
        }
        catch (NpgsqlException ex) { throw new StorageUnavailableException("Revision summaries could not be read.", ex); }
    }

    public async ValueTask<DocumentRevisionRead?> ReadRevisionSummaryAsync(Guid resourceId, ulong revisionNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand($"""
                SELECT {ReadHeadColumns},r.revision_number,r.summary_json
                FROM cellbridge_documents d {ReadHeadJoins}
                LEFT JOIN cellbridge_revision_summaries r ON r.resource_id=d.resource_id
                    AND r.lifecycle_generation=h.lifecycle_generation AND r.revision_number=$2
                WHERE d.resource_id=$1 AND NOT d.is_deleted
                """);
            command.Parameters.AddWithValue(resourceId);
            command.Parameters.AddWithValue((decimal)revisionNumber);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var metadata = DecodeReadMetadata(reader);
            var revision = reader.IsDBNull(ReadHeadColumnCount) ? null : DecodeRevisionSummary(reader, metadata);
            if (await reader.ReadAsync(cancellationToken)) throw new StorageCorruptionException("Multiple selected revision projections.");
            return new(metadata, revision);
        }
        catch (NpgsqlException ex) { throw new StorageUnavailableException("Revision summary could not be read.", ex); }
    }

    private static DocumentReadMetadata DecodeReadMetadata(NpgsqlDataReader reader)
    {
        if (reader.IsDBNull(4) || reader.IsDBNull(7))
            throw new StorageCorruptionException("The current metadata projection is missing or stale.");
        var metadata = RecoveryJson.Read<DocumentReadMetadata>(Encoding.UTF8.GetBytes(reader.GetString(7)));
        if (metadata.ResourceId != reader.GetGuid(0) || metadata.PathKey != reader.GetString(1) ||
            metadata.StateVersion != reader.GetInt64(2) || metadata.StateVersion != reader.GetInt64(4) ||
            metadata.IsDeleted != reader.GetBoolean(3) || metadata.LifecycleGeneration != reader.GetInt64(5) ||
            metadata.Security is null || metadata.Security.Grants is null || metadata.EditorExpiries.IsDefault ||
            metadata.PendingDeliveryCount < 0 ||
            string.IsNullOrWhiteSpace(metadata.Path) || metadata.ContentVersion == 0 || metadata.LifecycleGeneration < 1 ||
            metadata.CreatedUtc.Kind != DateTimeKind.Utc || metadata.ModifiedUtc.Kind != DateTimeKind.Utc ||
            metadata.RevisionCount != reader.GetInt32(6) || reader.GetInt32(6) < 1 || reader.GetInt64(8) != reader.GetInt32(6) ||
            reader.GetInt64(9) != reader.GetInt32(6) || reader.IsDBNull(10) ||
            reader.GetDecimal(10) != (decimal)metadata.LatestRevisionNumber)
            throw new StorageCorruptionException("The current metadata projection disagrees with its pointer or history.");
        ValidateReadContent(metadata.Content);
        return metadata;
    }

    private static DocumentRevisionSummary DecodeRevisionSummary(NpgsqlDataReader reader, DocumentReadMetadata metadata)
    {
        if (reader.IsDBNull(ReadHeadColumnCount + 1)) throw new StorageCorruptionException("Missing revision summary JSON.");
        var revision = RecoveryJson.Read<DocumentRevisionSummary>(Encoding.UTF8.GetBytes(reader.GetString(ReadHeadColumnCount + 1)));
        if (revision.ResourceId != metadata.ResourceId || revision.LifecycleGeneration != metadata.LifecycleGeneration ||
            (decimal)revision.RevisionNumber != reader.GetDecimal(ReadHeadColumnCount) ||
            revision.RevisionNumber == 0 || revision.RevisionNumber > metadata.LatestRevisionNumber ||
            revision.ContentVersion == 0 || revision.CreatedUtc.Kind != DateTimeKind.Utc ||
            revision.Author is null || string.IsNullOrWhiteSpace(revision.Author.Subject))
            throw new StorageCorruptionException("The revision summary disagrees with its current history.");
        ValidateReadContent(revision.Content);
        return revision;
    }

    private static void ValidateReadContent(ContentHandle? content)
    {
        if (content is null || string.IsNullOrWhiteSpace(content.Key) || content.Length < 0 || content.Sha256 is null ||
            content.Sha256.Length != 64 || content.Sha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new StorageCorruptionException("Invalid content metadata in read projection.");
    }

    private static async Task WriteSelectiveReadsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DocumentState state, CancellationToken cancellationToken)
    {
        // Recovery may supply retained snapshots in any order. Only the newest
        // state can supply the current index; the pointer is published in this transaction.
        await using (var current = new NpgsqlCommand("SELECT state_version FROM cellbridge_read_heads WHERE resource_id=$1", connection, transaction))
        {
            current.Parameters.AddWithValue(state.ResourceId);
            if (await current.ExecuteScalarAsync(cancellationToken) is long existing && existing > state.StateVersion) return;
        }
        var metadata = DocumentReadModels.Metadata(state);
        var revisions = DocumentReadModels.Revisions(state).ToArray();
        if (revisions.Length == 0 || revisions.Length != metadata.RevisionCount || revisions.Any(revision => revision.ResourceId != state.ResourceId ||
            revision.LifecycleGeneration != state.LifecycleGeneration || revision.RevisionNumber == 0) ||
            revisions.Select(revision => revision.RevisionNumber).Distinct().Count() != revisions.Length ||
            revisions.Max(revision => revision.RevisionNumber) != metadata.LatestRevisionNumber)
            throw new StorageCorruptionException("Cannot index inconsistent revision history.");
        var revisionJson = JsonSerializer.Serialize(revisions);
        // Compare the complete current membership, including removals made by an
        // explicit imported state. Unchanged immutable summaries are not rewritten.
        await using (var sync = new NpgsqlCommand("""
            WITH removed AS (DELETE FROM cellbridge_revision_summaries r WHERE r.resource_id=$1 AND NOT EXISTS (
                SELECT 1 FROM jsonb_array_elements($2::jsonb) item
                WHERE (item->>'LifecycleGeneration')::bigint=r.lifecycle_generation
                    AND (item->>'RevisionNumber')::numeric=r.revision_number))
            INSERT INTO cellbridge_revision_summaries(resource_id,lifecycle_generation,revision_number,summary_json)
                SELECT $1,(item->>'LifecycleGeneration')::bigint,(item->>'RevisionNumber')::numeric,item::text
                FROM jsonb_array_elements($2::jsonb) item
                ON CONFLICT(resource_id,lifecycle_generation,revision_number) DO UPDATE
                    SET summary_json=excluded.summary_json
                    WHERE cellbridge_revision_summaries.summary_json<>excluded.summary_json
            """, connection, transaction))
        {
            sync.Parameters.AddWithValue(state.ResourceId);
            sync.Parameters.AddWithValue(revisionJson);
            await sync.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var head = new NpgsqlCommand("""
            INSERT INTO cellbridge_read_heads(resource_id,state_version,lifecycle_generation,revision_count,metadata_json)
                VALUES($1,$2,$3,$4,$5)
                ON CONFLICT(resource_id) DO UPDATE SET state_version=excluded.state_version,
                    lifecycle_generation=excluded.lifecycle_generation,revision_count=excluded.revision_count,
                    metadata_json=excluded.metadata_json
            """, connection, transaction);
        head.Parameters.AddWithValue(state.ResourceId);
        head.Parameters.AddWithValue(state.StateVersion);
        head.Parameters.AddWithValue(state.LifecycleGeneration);
        head.Parameters.AddWithValue(revisions.Length);
        head.Parameters.AddWithValue(JsonSerializer.Serialize(metadata));
        await head.ExecuteNonQueryAsync(cancellationToken);
    }
}
