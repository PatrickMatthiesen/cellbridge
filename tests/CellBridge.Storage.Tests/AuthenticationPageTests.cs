using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using CellBridge.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CellBridge.Storage.Tests;

public sealed class AuthenticationPageTests
{
    [Fact]
    public async Task ApplicationNameAndReturnUrlAreEncodedInTheDefaultPage()
    {
        const string name = "Research <Portal> & \"Team\"";
        const string returnUrl = "/library?name=\"<document>\"";
        await using var app = await Start(name);
        using var client = app.GetTestClient();
        client.BaseAddress = new("https://localhost");
        using var response = await client.GetAsync("/auth/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1>Sign in to " + HtmlEncoder.Default.Encode(name) + "</h1>", html);
        Assert.DoesNotContain(name, html);
        Assert.Contains("name=\"returnUrl\" value=\"" + HtmlEncoder.Default.Encode(returnUrl) + "\"", html);
        Assert.NotEmpty(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
    }

    [Fact]
    public async Task CustomPageReceivesAntiforgeryAndValidatedReturnUrl()
    {
        LoginPageContext? page = null;
        await using var app = await Start("Example", context =>
        {
            page = context;
            return "<!doctype html><title>Custom sign in</title>";
        });
        using var client = app.GetTestClient();
        client.BaseAddress = new("https://localhost");
        using var response = await client.GetAsync("/auth/login?failed=1&returnUrl=//evil.test");
        Assert.Equal("<!doctype html><title>Custom sign in</title>", await response.Content.ReadAsStringAsync());
        Assert.NotNull(page);
        Assert.Equal("Example", page.ApplicationName);
        Assert.True(page.SignInFailed);
        Assert.Equal("/auth/complete", page.ReturnUrl);
        Assert.Equal("__RequestVerificationToken", page.Antiforgery.FormFieldName);
        Assert.NotEmpty(page.Antiforgery.RequestToken!);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(AuthenticationServices.AntiforgeryCookie + "="));
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task BlankApplicationNameIsRejectedAtStartup()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => Start(" "));
    }

    private static async Task<WebApplication> Start(string applicationName,
        Func<LoginPageContext, string>? renderer = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Page rendering requires no account database access.
            ["ConnectionStrings:cellbridge"] = "Host=localhost;Database=unused",
            ["Authentication:PublicOrigin"] = "https://localhost",
            ["Authentication:ApplicationName"] = applicationName,
        });
        builder.Services.AddCellBridgeAuthentication(builder.Configuration);
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapCellBridgeAuthentication(renderer);
        try
        {
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
