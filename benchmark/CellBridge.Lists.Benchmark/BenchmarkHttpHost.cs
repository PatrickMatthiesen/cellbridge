using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CellBridge.Lists;
using CellBridge.Lists.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal sealed class BenchmarkHttpHost : IAsyncDisposable
{
    private const string Scheme = "BenchmarkBearer";
    private const string ExecutionHeader = "X-Benchmark-Sql-Executions";
    private const string CommitHeader = "X-Benchmark-Sql-Commits";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    private readonly WebApplication app;
    private readonly HttpClient client;
    private BenchmarkHttpHost(WebApplication app, HttpClient client) { this.app = app; this.client = client; }

    public static async Task<BenchmarkHttpHost> StartAsync(IListEngine engine, ListActor actor)
    {
        var credentials = new Credentials(actor.Subject, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(engine);
        builder.Services.AddSingleton(credentials);
        builder.Services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, BearerHandler>(Scheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddCellBridgeLists(options =>
        {
            options.ActorResolver = context => context.User.Identity?.IsAuthenticated == true
                && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } subject ? new(subject) : null;
            options.IntegrationAuthenticationScheme = Scheme;
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // AsyncLocal client scopes do not flow through HTTP. Measure on the server.
            using var measurement = SqlMeasurements.Begin();
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[ExecutionHeader] = measurement.Executions.ToString(CultureInfo.InvariantCulture);
                context.Response.Headers[CommitHeader] = measurement.Commits.ToString(CultureInfo.InvariantCulture);
                return Task.CompletedTask;
            });
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCellBridgeLists();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not publish a benchmark address.");
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, MaxConnectionsPerServer = 128 })
        { BaseAddress = new Uri(addresses.Addresses.Single()), Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        return new(app, client);
    }

    public async Task<T> SendAsync<T>(string path, HttpMethod method, object body, OperationMetrics metrics)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        metrics.RequestBodyBytes = bytes.Length;
        using var request = new HttpRequestMessage(method, path)
        { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request);
        metrics.SqlExecutions = Header(response, ExecutionHeader);
        metrics.SqlCommits = Header(response, CommitHeader);
        var content = await response.Content.ReadAsByteArrayAsync();
        metrics.ResponseBodyBytes = content.Length;
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<T>(content, Json) ?? throw new InvalidOperationException("The response was empty.");
    }

    private static int Header(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) && int.TryParse(values.Single(), out var result)
            ? result : throw new InvalidOperationException("The HTTP SQL measurement header is missing.");

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private sealed record Credentials(string Subject, string Token);
    private sealed class BearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, Credentials credentials) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[7..]), Encoding.UTF8.GetBytes(credentials.Token)))
                return Task.FromResult(AuthenticateResult.Fail("Invalid local benchmark token."));
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, credentials.Subject)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}

internal sealed class OperationMetrics
{
    public int SqlExecutions;
    public int SqlCommits;
    public long RequestBodyBytes;
    public long ResponseBodyBytes;
}
