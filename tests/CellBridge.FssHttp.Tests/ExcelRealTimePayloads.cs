using System.Text.Json;

namespace CellBridge.FssHttp.Tests;

// Synthetic payloads built from the checked native writer's key families.
// These are not captures or evidence of unfinished cell-text transmission.
public static class ExcelRealTimePayloads
{
    public static IEnumerable<object[]> Notifications()
    {
        const string id = "81113e72-4b42-4e83-8c87-45c5306ce8ea";
        foreach (int clientType in new[] { 0, 2 })
        {
            foreach ((int type, string metadata) in new (int, string)[]
            {
                (2, $"\"GeneratedXluid\":\"{id}\",\"CollabMode\":1,\"OcsCapable\":false,\"SupportsChat\":null"),
                (3, $"\"SeqNum\":4294967295,\"SessionId\":\"{id}\",\"Rev\":{{\"value\":1}}"),
                (5, $"\"FullSaveId\":\"{id}\""),
                (6, $"\"ClientId\":\"{id}\",\"SavRev\":123"),
                (7, $"\"ClientId\":\"{id}\",\"SavRev\":124,\"Blobs\":[{{\"Hash\":\"synthetic\",\"Size\":16,\"Ttl\":30}}]"),
                (8, $"\"ClientId\":\"{id}\""),
                (9, $"\"Nsv\":\"{id}\""),
                (10, "\"Extension\":{\"synthetic\":true}")
            })
            {
                string payload = $"{{\"Version\":2,\"ClientType\":{clientType},\"PresenceType\":{type},{metadata}}}";
                yield return new object[] { type == 3 ? 8 : 3, payload };
                if (type == 6)
                    yield return new object[] { 3, payload.Replace("\"SavRev\":123", "\"SavRev\":18446744073709551614") };
            }
        }
    }

    public static IEnumerable<object[]> InvalidNotifications()
    {
        foreach (object[] notification in Notifications())
        {
            int channel = (int)notification[0];
            string payload = (string)notification[1];
            yield return new object[] { channel, payload.Replace("\"Version\":2", "\"Version\":1") };
            yield return new object[] { channel, WithProperty(payload, "A", null) };
            yield return new object[] { channel, WithProperty(payload, "clientId", "not-a-guid") };
            yield return new object[] { channel, WithProperty(payload, "Cid", Guid.Empty) };
            yield return new object[] { channel, WithProperty(payload, "Version", 2) }; // Duplicate key.
            yield return new object[] { channel, WithProperty(payload, "Extension", new string('x', 64 * 1024)) };
            yield return new object[] { channel, payload[..^1] + ",\"Extension\":{" + string.Concat(Enumerable.Repeat("\"nested\":{", 17)) + "\"value\":1" + new string('}', 19) };
        }
        yield return new object[] { 3, "{\"Version\":2,\"ClientType\":0,\"PresenceType\":6,\"ClientId\":\"81113e72-4b42-4e83-8c87-45c5306ce8ea\",\"SavRev\":-1}" };
        yield return new object[] { 3, "{\"Version\":2,\"ClientType\":0,\"PresenceType\":6,\"ClientId\":\"81113e72-4b42-4e83-8c87-45c5306ce8ea\",\"SavRev\":18446744073709551615}" };
        yield return new object[] { 3, "{\"Version\":2,\"ClientType\":0,\"PresenceType\":8,\"ClientId\":null}" };
        yield return new object[] { 8, "{\"Version\":2,\"ClientType\":0,\"PresenceType\":3,\"SeqNum\":4294967296,\"SessionId\":\"synthetic\"}" };
        yield return new object[] { 8, "{\"Version\":2,\"ClientType\":0,\"PresenceType\":3,\"SeqNum\":1,\"SessionId\":\"\"}" };
    }

    public static string WithProperty(string payload, string name, object? value) =>
        payload[..^1] + "," + JsonSerializer.Serialize(name) + ":" + JsonSerializer.Serialize(value) + "}";
}
