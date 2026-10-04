using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.Authentication;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapCellBridgeAuthentication(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/login", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var returnUrl = LocalReturnUrl(context.Request.Query["returnUrl"]);
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Content(LoginPage.Render(tokens, returnUrl, context.Request.Query.ContainsKey("failed")), "text/html");
        }).AllowAnonymous();
        app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery, SignInManager<CellBridgeUser> signIn) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var returnUrl = LocalReturnUrl(form["returnUrl"]);
            var login = form["login"].ToString();
            var password = form["password"].ToString();
            if (login.Length > 256 || password.Length > 1024) return Results.BadRequest();
            var result = await signIn.PasswordSignInAsync(login, password, isPersistent: false, lockoutOnFailure: true);
            return result.Succeeded ? Results.LocalRedirect(returnUrl)
                : Results.LocalRedirect("/auth/login?failed=1&returnUrl=" + Uri.EscapeDataString(returnUrl));
        }).AllowAnonymous();
        app.MapGet("/auth/complete", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return context.User.Identity?.IsAuthenticated == true
                ? Results.Content("<!doctype html><title>Signed in</title><p>Signed in to CellBridge.</p><a href=\"/library\">Open the library</a>", "text/html")
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
