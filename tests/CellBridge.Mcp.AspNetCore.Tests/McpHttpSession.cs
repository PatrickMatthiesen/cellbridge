using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CellBridge.Mcp.AspNetCore.Tests;

internal sealed class McpHttpSession(HttpClient http)
{
    private const string Endpoint = "/mcp";
    private const string SessionHeader = "Mcp-Session-Id";
    private const string ProtocolHeader = "MCP-Protocol-Version";
    private const string RequestedProtocolVersion = "2025-06-18";
    private string? sessionId;
    private string? protocolVersion;
    private int requestId;

    public const string InitializeMethod = "initialize";
    public static object InitializeArguments => new
    {
        protocolVersion = RequestedProtocolVersion,
        capabilities = new { },
        clientInfo = new { name = "cellbridge-mcp-tests", version = "1.0" },
    };

    public async Task InitializeAsync()
    {
        using var response = await SendAsync(InitializeMethod, InitializeArguments);
        var result = AssertResult(await ReadMessageAsync(response));
        protocolVersion = result.GetProperty("protocolVersion").GetString();
        Assert.False(string.IsNullOrEmpty(protocolVersion));
        sessionId = Assert.Single(response.Headers.GetValues(SessionHeader));
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        using var notification = await SendAsync("notifications/initialized", notification: true);
        Assert.Equal(HttpStatusCode.Accepted, notification.StatusCode);
    }

    public async Task<JsonElement> CallAsync(string name, object arguments)
    {
        using var response = await SendAsync("tools/call", new { name, arguments });
        return AssertResult(await ReadMessageAsync(response));
    }

    public Task<HttpResponseMessage> SendAsync(string method, object? parameters = null,
        bool notification = false, HttpClient? requestClient = null, string? origin = null)
    {
        var body = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method };
        if (!notification)
            body["id"] = Interlocked.Increment(ref requestId);
        if (parameters is not null)
            body["params"] = parameters;
        return SendAsync(HttpMethod.Post, JsonContent.Create(body), requestClient, origin);
    }

    public async Task EndAsync()
    {
        using var response = await SendAsync(HttpMethod.Delete);
        response.EnsureSuccessStatusCode();
    }

    public static async Task<JsonElement> ReadMessageAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"MCP HTTP response {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}", null, response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                    return Parse(line["data:".Length..]);
            }
            throw new InvalidOperationException("The MCP event stream closed without a JSON-RPC message.");
        }
        return Parse(await response.Content.ReadAsStringAsync());
    }

    public static JsonElement AssertResult(JsonElement message)
    {
        Assert.Equal("2.0", message.GetProperty("jsonrpc").GetString());
        Assert.False(message.TryGetProperty("error", out _), message.ToString());
        return message.GetProperty("result");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, HttpContent? content = null,
        HttpClient? requestClient = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(method, Endpoint) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (sessionId is not null)
            request.Headers.Add(SessionHeader, sessionId);
        if (protocolVersion is not null)
            request.Headers.Add(ProtocolHeader, protocolVersion);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return await (requestClient ?? http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static JsonElement Parse(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }
}
