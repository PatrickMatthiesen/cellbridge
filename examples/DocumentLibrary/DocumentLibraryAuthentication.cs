using System.Net;
using System.Security.Claims;
using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CellBridge.DocumentLibrary;

internal static class DocumentLibraryAuthentication
{
    public static bool IsRequestAllowed(HttpContext context, IHostEnvironment environment) =>
        (environment.IsDevelopment() || environment.IsEnvironment("Testing")) &&
        (context.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(context.Connection.RemoteIpAddress));
}

// Disposable private-test accounts. Real applications supply their own account system.
internal sealed class LocalLoginAuthenticator(IHostEnvironment environment) : ICellBridgeLoginAuthenticator
{
    public Task<ClaimsPrincipal?> AuthenticateAsync(HttpContext context, string username, string password,
        CancellationToken cancellationToken)
    {
        if (!DocumentLibraryAuthentication.IsRequestAllowed(context, environment) || password != "Test1234!")
            return Task.FromResult<ClaimsPrincipal?>(null);
        var subject = username.ToLowerInvariant() switch
        {
            "ofba-operator" or "owner" => DocumentLibraryService.OwnerSubject,
            "editor" => DocumentLibraryService.EditorSubject,
            "reader" => DocumentLibraryService.ReaderSubject,
            _ => null,
        };
        if (subject is null) return Task.FromResult<ClaimsPrincipal?>(null);
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(CellBridgeActor.SubjectClaim, subject),
            new(CellBridgeActor.DisplayNameClaim, username),
        };
        if (subject == DocumentLibraryService.OwnerSubject)
        {
            claims.Add(new(CellBridgeActor.CreateClaim, "true"));
            claims.Add(new("document-library:permission-admin", "true"));
        }
        return Task.FromResult<ClaimsPrincipal?>(new(new ClaimsIdentity(claims,
            CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role)));
    }
}
