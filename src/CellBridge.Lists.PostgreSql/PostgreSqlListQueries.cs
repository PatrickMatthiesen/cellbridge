using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace CellBridge.Lists.PostgreSql;

public sealed partial class PostgreSqlListEngine
{
    /// <summary>Filters, exact typed ordering and keyset paging execute in PostgreSQL. Only a page is materialized.</summary>
    public Task<RecordPage> QueryAsync(ListActor actor, Guid workspaceId, Guid listId, ListQuery query,
        CancellationToken ct = default)
    {
        ValidateActor(actor);
        return InRecordTransactionAsync(actor, workspaceId, async (connection, transaction, authorization) =>
        {
            var context = await ReadWriteContextAsync(connection, transaction, workspaceId, listId,
                Guid.Empty, actor.Subject, ListAccess.Read, deleteLock: false, ct, authorization);
            RequireWriteContext(context, ListAccess.Read);
            var definition = context.Definition!;
            ListValidation.ValidateQuery(definition, query, _limits);
            await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
            var workspace = QueryParameter(command, NpgsqlDbType.Uuid, workspaceId);
            var list = QueryParameter(command, NpgsqlDbType.Uuid, listId);
            var cursor = QueryParameter(command, NpgsqlDbType.Uuid, query.AfterRecordId);
            var pageLimit = QueryParameter(command, NpgsqlDbType.Bigint, (long)query.Limit + 1);
            var fields = definition.Fields.ToDictionary(field => field.Id);
            var keys = new Dictionary<Guid, string>();
            string Raw(ListField field)
            {
                if (!keys.TryGetValue(field.Id, out var key))
                {
                    key = QueryParameter(command, NpgsqlDbType.Text, field.Id.ToString("D"));
                    keys.Add(field.Id, key);
                }
                return $"(r.values_json ->> {key})";
            }
            var predicates = new List<string> { $"r.workspace_id={workspace}", $"r.list_id={list}" };
            foreach (var filter in query.Filters ?? [])
            {
                var field = fields[filter.FieldId];
                var raw = Raw(field);
                if (filter.Value is null)
                {
                    predicates.Add($"{raw} IS {(filter.Operator == FilterOperator.NotEqual ? "NOT " : "")}NULL");
                    continue;
                }
                var ordered = filter.Operator is not FilterOperator.Equal and not FilterOperator.NotEqual;
                var expression = QueryScalarExpression(field.Type, raw, ordered);
                var (type, value) = QueryScalarParameter(field.Type, filter.Value, ordered);
                var parameter = QueryParameter(command, type, value);
                // C equality has the same exact string identity as .NET ordinal equality;
                // only ordered text comparisons require UTF-16 code-unit keys.
                if (type == NpgsqlDbType.Text) parameter += " COLLATE \"C\"";
                var op = filter.Operator switch
                {
                    FilterOperator.Equal => "IS NOT DISTINCT FROM",
                    FilterOperator.NotEqual => "IS DISTINCT FROM",
                    FilterOperator.GreaterThan => ">",
                    FilterOperator.GreaterOrEqual => ">=",
                    FilterOperator.LessThan => "<",
                    FilterOperator.LessOrEqual => "<=",
                    _ => throw Validation("Unknown query operator.")
                };
                predicates.Add($"{expression} {op} {parameter}");
            }
            var sort = query.SortFieldId is { } sortId
                ? QueryScalarExpression(fields[sortId].Type, Raw(fields[sortId]), ordered: true) : "r.record_id";
            var comparison = query.Descending ? "<" : ">";
            var direction = query.Descending ? "DESC NULLS LAST" : "ASC NULLS FIRST";
            // Null placement is part of the cursor order, not PostgreSQL's default.
            var after = query.SortFieldId is null
                ? $"r.record_id {comparison} a.record_id"
                : query.Descending
                    ? $"(({sort} IS NULL AND a.sort_key IS NOT NULL) OR {sort} < a.sort_key OR ({sort} IS NOT DISTINCT FROM a.sort_key AND r.record_id < a.record_id))"
                    : $"(({sort} IS NOT NULL AND a.sort_key IS NULL) OR {sort} > a.sort_key OR ({sort} IS NOT DISTINCT FROM a.sort_key AND r.record_id > a.record_id))";
            var where = string.Join(" AND ", predicates.Select(predicate => "(" + predicate + ")"));
            command.CommandText = $"""
                WITH anchor AS MATERIALIZED (
                  SELECT r.record_id,{sort} AS sort_key
                  FROM cellbridge_lists.records r
                  WHERE {where} AND r.record_id={cursor}
                ), page AS (
                  SELECT r.record_id,r.schema_version,r.revision,r.number,r.values_json::text,
                         r.created_utc,r.modified_utc,r.created_by,r.modified_by,{sort} AS sort_key
                  FROM cellbridge_lists.records r
                  LEFT JOIN anchor a ON true
                  WHERE {where} AND ({cursor} IS NULL OR (a.record_id IS NOT NULL AND ({after})))
                  ORDER BY {sort} {direction},r.record_id {direction}
                  LIMIT {pageLimit}
                )
                SELECT p.record_id,p.schema_version,p.revision,p.number,p.values_json,
                       p.created_utc,p.modified_utc,p.created_by,p.modified_by,
                       ({cursor} IS NULL OR EXISTS(SELECT 1 FROM anchor)) AS cursor_valid
                FROM (SELECT 1) seed LEFT JOIN page p ON true
                ORDER BY p.sort_key {direction},p.record_id {direction}
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            var records = new List<ListRecord>(query.Limit);
            var hasMore = false;
            while (await reader.ReadAsync(ct))
            {
                if (!reader.GetBoolean(9)) throw Validation("Page cursor is no longer present in this filtered list.");
                if (reader.IsDBNull(0)) continue;
                if (records.Count == query.Limit) { hasMore = true; break; }
                records.Add(ReadRecord(reader, workspaceId, listId));
            }
            return new RecordPage(records, hasMore ? records[^1].Id : null, definition.SchemaVersion);
        }, ct);
    }

    private static string QueryParameter(NpgsqlCommand command, NpgsqlDbType type, object? value)
    {
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value });
        return "$" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
    }

    private static string QueryScalarExpression(ListFieldType type, string raw, bool ordered) => type switch
    {
        ListFieldType.Integer => $"({raw})::bigint",
        ListFieldType.Decimal => $"({raw})::numeric",
        ListFieldType.Boolean => $"({raw})::boolean",
        ListFieldType.Text or ListFieldType.Choice when ordered => QueryOrdinalExpression(raw),
        ListFieldType.Timestamp => QueryTimestampExpression(raw),
        _ => $"{raw} COLLATE \"C\""
    };

    private static (NpgsqlDbType Type, object Value) QueryScalarParameter(ListFieldType type, JsonNode node, bool ordered)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        var json = document.RootElement;
        return type switch
        {
            ListFieldType.Integer => (NpgsqlDbType.Bigint, json.GetInt64()),
            ListFieldType.Decimal => (NpgsqlDbType.Numeric, json.GetDecimal()),
            ListFieldType.Boolean => (NpgsqlDbType.Boolean, json.GetBoolean()),
            ListFieldType.Text or ListFieldType.Choice when ordered => (NpgsqlDbType.Bytea, Encoding.BigEndianUnicode.GetBytes(json.GetString()!)),
            ListFieldType.Timestamp => (NpgsqlDbType.Text, QueryTimestampKey(json.GetString()!)),
            _ => (NpgsqlDbType.Text, json.GetString()!)
        };
    }

    private static string QueryTimestampKey(string value) => value[..19] +
        (value.Length > 20 ? value[20..^1] : "").PadRight(7, '0');

    // PostgreSQL timestamptz stores microseconds. Preserve the accepted seventh
    // fractional digit by ordering the canonical UTC seconds and padded fraction.
    private static string QueryTimestampExpression(string raw) => $"""
        (CASE WHEN {raw} IS NULL THEN NULL ELSE
          substring({raw} FROM 1 FOR 19) ||
          rpad(CASE WHEN length({raw})>20 THEN substring({raw} FROM 21 FOR length({raw})-21) ELSE '' END,7,'0')
         END) COLLATE "C"
        """;

    // UTF-8/C collation puts supplementary codepoints after BMP characters. .NET
    // ordinal compares UTF-16 code units, so encode surrogate pairs explicitly.
    // Keep this server-side expression limited to ordered text operations.
    private static string QueryOrdinalExpression(string raw) => $"""
        CASE WHEN {raw} IS NULL THEN NULL ELSE decode(coalesce((
          SELECT string_agg(CASE WHEN ascii(substring({raw} FROM utf16.n FOR 1))<=65535
            THEN lpad(to_hex(ascii(substring({raw} FROM utf16.n FOR 1))),4,'0')
            ELSE lpad(to_hex(((ascii(substring({raw} FROM utf16.n FOR 1))-65536)>>10)+55296),4,'0') ||
                 lpad(to_hex(((ascii(substring({raw} FROM utf16.n FOR 1))-65536)&1023)+56320),4,'0')
            END,'' ORDER BY utf16.n)
          FROM generate_series(1,char_length({raw})) utf16(n)
        ),''),'hex') END
        """;
}
