using System.Text.Encodings.Web;
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
            return Results.Content(LoginHtml(tokens, returnUrl, context.Request.Query.ContainsKey("failed")), "text/html");
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

    private static string LoginHtml(AntiforgeryTokenSet tokens, string returnUrl, bool failed)
    {
        var encode = HtmlEncoder.Default;
        return $"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Sign in to CellBridge</title></head><body><main><h1>Sign in to CellBridge</h1>
            {(failed ? "<p role=\"alert\">Sign-in failed. Check your credentials or try again later.</p>" : "")}
            <form method="post" action="/auth/login">
            <input type="hidden" name="{encode.Encode(tokens.FormFieldName)}" value="{encode.Encode(tokens.RequestToken!)}">
            <input type="hidden" name="returnUrl" value="{encode.Encode(returnUrl)}">
            <p><label>Username <input name="login" autocomplete="username" maxlength="256" required></label></p>
            <p><label>Password <input name="password" type="password" autocomplete="current-password" maxlength="1024" required></label></p>
            <button type="submit">Sign in</button></form></main></body></html>
            """;
    }
}
