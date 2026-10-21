using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;

namespace CellBridge.HostAuthorization.Tests;

public sealed class HostAuthorizationTests : IAsyncLifetime
{
    private DistributedApplication? application;
    private HttpClient? http;
    private readonly string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(48));

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(
            ["--environment", "Testing"], timeout.Token);
        builder.Configuration["Parameters:SigningKey"] = key;
        application = await builder.BuildAsync(timeout.Token);
        try
        {
            await application.StartAsync(timeout.Token);
            await application.ResourceNotifications.WaitForResourceHealthyAsync("authorization-consumer", timeout.Token);
            http = application.CreateHttpClient("authorization-consumer", "http");
        }
        catch { await DisposeAsync(); throw; }
    }

    [Fact]
    public async Task HostAuthorityControlsDownloadsCatalogAndCoordinatedRevocation()
    {
        await Request("writer", "/shared/example.txt", HttpStatusCode.OK);
        await Request("reader", "/shared/example.txt", HttpStatusCode.OK);
        await Request("denied", "/shared/example.txt", HttpStatusCode.NotFound);
        Assert.Single((await Request("writer", "/example/catalog", HttpStatusCode.OK)).GetProperty("documents").EnumerateArray());
        Assert.Empty((await Request("denied", "/example/catalog", HttpStatusCode.OK)).GetProperty("documents").EnumerateArray());
        const string permissions = "/example/permissions/0a0a19bc-9b47-4183-8764-41c117bb1f14";
        var update = new { expectedRevision = 1, nextRevision = 2, access = "none" };
        await Request("writer", permissions, HttpStatusCode.Forbidden, update);
        await Request("permission-operator", permissions, HttpStatusCode.OK, update);
        await Request("writer", "/shared/example.txt", HttpStatusCode.NotFound);
        await Request("reader", "/shared/example.txt", HttpStatusCode.OK);
        Assert.Empty((await Request("writer", "/example/catalog", HttpStatusCode.OK)).GetProperty("documents").EnumerateArray());
    }

    private async Task<JsonElement> Request(string subject, string path, HttpStatusCode expected, object? body = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(subject));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http!.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType != "application/json") return default;
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private string Token(string subject)
    {
        static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "cellbridge-host-example", aud = "cellbridge", sub = subject,
            exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        }));
        var data = header + "." + payload;
        return data + "." + Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data)));
    }

    public async Task DisposeAsync()
    {
        http?.Dispose();
        if (application is not null) { await application.DisposeAsync(); application = null; }
    }
}
