using System.Security.Claims;
using CellBridge.AspNetCore;

public sealed class ExampleLoginAuthenticator : ICellBridgeLoginAuthenticator
{
    public Task<ClaimsPrincipal?> AuthenticateAsync(HttpContext context, string username, string password,
        CancellationToken cancellationToken)
    {
        if (password != "example-only" || username is not ("owner" or "reader"))
            return Task.FromResult<ClaimsPrincipal?>(null);

        var subject = "example:" + username;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(CellBridgeActor.SubjectClaim, subject),
            new(CellBridgeActor.DisplayNameClaim, username)
        };
        if (username == "owner") claims.Add(new(CellBridgeActor.CreateClaim, "true"));
        var identity = new ClaimsIdentity(claims, "CellBridge");
        return Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(identity));
    }
}
