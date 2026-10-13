using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CellBridge.Authentication;

public static class AuthenticationEndpoints
{
    /// <summary>Maps the sample sign-in endpoints, optionally replacing only the login page HTML.</summary>
    public static IEndpointRouteBuilder MapCellBridgeAuthentication(this IEndpointRouteBuilder app,
        Func<LoginPageContext, string>? renderLoginPage = null)
    {
        app.MapGet("/auth/login", (HttpContext context, IAntiforgery antiforgery, IOptions<AuthenticationPageOptions> options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var returnUrl = LocalReturnUrl(context.Request.Query["returnUrl"]);
            var tokens = antiforgery.GetAndStoreTokens(context);
            var page = new LoginPageContext(options.Value.ApplicationName, tokens, returnUrl,
                context.Request.Query.ContainsKey("failed"));
            return Results.Content((renderLoginPage ?? LoginPage.Render)(page), "text/html");
        }).AllowAnonymous();
        app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery, SignInManager<CellBridgeUser> signIn) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.HasFormContentType ||
                context.Request.ContentType?.StartsWith("application/x-www-form-urlencoded",
                    StringComparison.OrdinalIgnoreCase) is not true ||
                context.Request.ContentLength > 16 * 1024)
                return Results.BadRequest();
            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync(new FormOptions
                {
                    ValueCountLimit = 16,
                    KeyLengthLimit = 256,
                    ValueLengthLimit = 4096,
                }, context.RequestAborted);
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            catch (InvalidDataException) { return Results.BadRequest(); }
            var returnUrl = LocalReturnUrl(form["returnUrl"]);
            var login = form["login"].ToString();
            var password = form["password"].ToString();
            if (login.Length > 256 || password.Length > 1024) return Results.BadRequest();
            var result = await signIn.PasswordSignInAsync(login, password, isPersistent: false, lockoutOnFailure: true);
            return result.Succeeded ? Results.LocalRedirect(returnUrl)
                : Results.LocalRedirect("/auth/login?failed=1&returnUrl=" + Uri.EscapeDataString(returnUrl));
        }).AllowAnonymous().RequireRateLimiting(AuthenticationServices.LoginRateLimitPolicy)
            .WithRequestTimeout(TimeSpan.FromSeconds(30));
        app.MapGet("/auth/complete", (HttpContext context, IOptions<AuthenticationPageOptions> options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return context.User.Identity?.IsAuthenticated == true
                ? Results.Content($"<!doctype html><title>Signed in</title><p>Signed in to {HtmlEncoder.Default.Encode(options.Value.ApplicationName)}.</p><a href=\"/library\">Open the library</a>", "text/html")
                : Results.Unauthorized();
        }).AllowAnonymous();
        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery, SignInManager<CellBridgeUser> signIn) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/auth/login?returnUrl=%2Flibrary");
        }).RequireAuthorization();
        return app;
    }

    public static string LocalReturnUrl(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith('/') && !value.StartsWith("//") && !value.Contains('\\') &&
        !value.Any(char.IsControl) ? value : "/auth/complete";
}
