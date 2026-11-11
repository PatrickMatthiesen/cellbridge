using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;

namespace CellBridge.AspNetCore;

/// <summary>Shared Microsoft v2 token validation for the packaged native and host browser flows.</summary>
/// <remarks>Audience, lifetime and signature checks remain enabled. Account-audience policy and host account
/// mapping run after these cryptographic checks; they must not replace issuer or signing-key validation.</remarks>
public static class CellBridgeEntraTokenValidation
{
    /// <summary>Enables Microsoft issuer and signing-key issuer validation for a fixed public-cloud authority.</summary>
    public static void Configure(TokenValidationParameters parameters, string authority)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "login.microsoftonline.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use a public-cloud Microsoft v2 authority.", nameof(authority));
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts[1] != "v2.0" ||
            parts[0] is not ("common" or "organizations" or "consumers") &&
            (!Guid.TryParse(parts[0], out var authorityTenant) || authorityTenant == Guid.Empty))
            throw new ArgumentException("Use a Microsoft tenant GUID, common, organizations or consumers v2 authority.", nameof(authority));

        var aad = AadIssuerValidator.GetAadIssuerValidator(authority);
        parameters.ValidateIssuer = true;
        parameters.ValidateIssuerSigningKey = true;
        parameters.ValidIssuer = Guid.TryParse(parts[0], out var fixedTenant)
            ? $"https://login.microsoftonline.com/{fixedTenant:D}/v2.0"
            : "https://login.microsoftonline.com/{tenantid}/v2.0";
        parameters.IssuerValidator = (issuer, token, validation) =>
        {
            if (!TryClaims(Claims(token), out var tenantId, out _))
                throw new SecurityTokenInvalidIssuerException("Microsoft v2 identity claims are missing, duplicated or invalid.");
            var expected = $"https://login.microsoftonline.com/{tenantId:D}/v2.0";
            if (!string.Equals(issuer, expected, StringComparison.Ordinal))
                throw new SecurityTokenInvalidIssuerException("The Microsoft issuer does not match its tenant claim.");
            return aad.Validate(issuer, token, validation);
        };
        parameters.EnableAadSigningKeyIssuerValidation();
    }

    /// <summary>Reads one validated Microsoft v2 identity without inferring links from email or display claims.</summary>
    public static bool TryIdentity(ClaimsPrincipal? principal, out Guid tenantId, out Guid objectId)
    {
        tenantId = objectId = Guid.Empty;
        return principal is not null && principal.Identities.Count(i => i.IsAuthenticated) == 1 &&
            TryClaims(principal.Claims, out tenantId, out objectId);
    }

    private static IEnumerable<Claim> Claims(SecurityToken token) => token switch
    {
        JsonWebToken json => json.Claims,
        JwtSecurityToken jwt => jwt.Claims,
        _ => [],
    };

    private static bool TryClaims(IEnumerable<Claim> claims, out Guid tenantId, out Guid objectId)
    {
        tenantId = objectId = Guid.Empty;
        var tenants = new List<string>();
        var objects = new List<string>();
        var versions = new List<string>();
        foreach (var claim in claims)
        {
            if (claim.Type == "tid") tenants.Add(claim.Value);
            else if (claim.Type == "oid") objects.Add(claim.Value);
            else if (claim.Type == "ver") versions.Add(claim.Value);
        }
        return tenants.Count == 1 && objects.Count == 1 && versions.Count == 1 && versions[0] == "2.0" &&
            Guid.TryParse(tenants[0], out tenantId) && tenantId != Guid.Empty &&
            Guid.TryParse(objects[0], out objectId) && objectId != Guid.Empty;
    }
}
