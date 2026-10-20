#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.Testing;

namespace CellBridge.Interop.Tests;

[CollectionDefinition("WebDemo integration", DisableParallelization = true)]
public sealed class WebDemoCollection : ICollectionFixture<WebDemoFixture>;

/// <summary>Exercises real login, HTTP protocol routing and a second host over an owned database.</summary>
public sealed class WebDemoFixture : IAsyncLifetime
{
    private DistributedApplication? application;
    private readonly Dictionary<string, string?> previous = new();

    public async Task InitializeAsync()
    {
        // A supplied endpoint remains available for focused external interoperability runs.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")))
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "Aa1!";
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.CellBridge_WebDemo_AppHost>(
            ["--environment", "Testing"], timeout.Token);
        builder.Configuration["Parameters:TestPassword"] = password;
        application = await builder.BuildAsync(timeout.Token);
        try
        {
            await application.StartAsync(timeout.Token);
            await Task.WhenAll(application.ResourceNotifications.WaitForResourceHealthyAsync("webdemo", timeout.Token),
                application.ResourceNotifications.WaitForResourceHealthyAsync("webdemo-peer", timeout.Token));
            var origin = application.GetEndpoint("webdemo", "https");
            Set("OFFICECOLLABSERVER_INTEROP_ENDPOINT", new Uri(origin, "/_vti_bin/cellstorage.svc").ToString());
            Set("OFFICECOLLABSERVER_INTEROP_PEER", new Uri(application.GetEndpoint("webdemo-peer", "https"), "/_vti_bin/cellstorage.svc").ToString());
            Set("CELLBRIDGE_INTEROP_PUBLIC_ORIGIN", "https://cellbridge.test");
            var cookies = new CookieContainer();
            using var http = new HttpClient(new HttpClientHandler
            {
                CookieContainer = cookies,
                ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri?.IsLoopback == true,
            }) { BaseAddress = origin };
            var token = await Token(http);
            using var login = await http.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["login"] = "integration-writer", ["password"] = password,
                ["__RequestVerificationToken"] = token, ["returnUrl"] = "/auth/complete",
            }), timeout.Token);
            login.EnsureSuccessStatusCode();
            if (login.RequestMessage?.RequestUri?.AbsolutePath != "/auth/complete")
                throw new InvalidOperationException("The disposable integration account could not sign in.");
            token = await Token(http);
            Set("CELLBRIDGE_INTEROP_COOKIE", cookies.GetCookieHeader(origin));
            Set("CELLBRIDGE_INTEROP_CSRF", token);
            http.DefaultRequestHeaders.Add("X-CellBridge-CSRF", token);
            foreach (var name in new[] { "test", "save-test", "save-check" })
            {
                using var response = await http.PostAsJsonAsync("/api/documents", new { name, type = "docx" }, timeout.Token);
                response.EnsureSuccessStatusCode();
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private static async Task<string> Token(HttpClient http)
    {
        var page = await http.GetStringAsync("/auth/login");
        var match = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        if (!match.Success) throw new InvalidOperationException("The login page did not issue an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private void Set(string name, string value)
    {
        previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public async Task DisposeAsync()
    {
        foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        if (application is not null) { await application.DisposeAsync(); application = null; }
    }
}
