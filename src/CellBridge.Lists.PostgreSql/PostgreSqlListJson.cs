using System.Text.Json;
using System.Text.Json.Nodes;

namespace CellBridge.Lists.PostgreSql;

internal static class PostgreSqlListJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new ListException(ListFailure.Unavailable, "Stored list data is invalid.");
    public static JsonObject Object(string json) => JsonNode.Parse(json)?.AsObject()
        ?? throw new ListException(ListFailure.Unavailable, "Stored record data is invalid.");

    public static string RecordValues(JsonObject values)
    {
        JsonObject? normalized = null;
        foreach (var property in values)
        {
            // Validation has already checked the field's exact numeric domain.
            // Zero may have an insignificant scale beyond PostgreSQL numeric's
            // representation limit. Persist its value without that scale while
            // keeping the caller's request and retry snapshot unchanged.
            if (IsNumericZero(property.Value))
            {
                normalized ??= (JsonObject)values.DeepClone();
                normalized[property.Key] = 0;
            }
        }
        return (normalized ?? values).ToJsonString();
    }

    public static ListQuery NormalizeQuery(ListQuery query)
    {
        if (query.Filters is null) return query;
        RecordFilter[]? normalized = null;
        for (var i = 0; i < query.Filters.Count; i++)
        {
            var filter = query.Filters[i];
            if (!IsNumericZero(filter.Value)) continue;
            normalized ??= query.Filters.ToArray();
            normalized[i] = filter with { Value = JsonValue.Create(0) };
        }
        return normalized is null ? query : query with { Filters = normalized };
    }

    private static bool IsNumericZero(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<decimal>(out var number) && number == 0m;
}
