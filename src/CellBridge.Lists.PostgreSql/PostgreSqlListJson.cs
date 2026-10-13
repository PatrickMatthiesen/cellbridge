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
}
