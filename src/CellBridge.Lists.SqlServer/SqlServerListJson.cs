using System.Text.Json;
using System.Text.Json.Nodes;

namespace CellBridge.Lists.SqlServer;

internal static class SqlServerListJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new ListException(ListFailure.Unavailable, "Stored list data is invalid.");
    internal static JsonObject Object(string json) => JsonNode.Parse(json)?.AsObject()
        ?? throw new ListException(ListFailure.Unavailable, "Stored record data is invalid.");
    internal static string RecordValues(JsonObject values) => values.ToJsonString();
    internal static JsonObject NormalizeOwnedRecordValues(JsonObject values) => Object(values.ToJsonString());
    internal static ListQuery NormalizeQuery(ListQuery query) => query;
}
