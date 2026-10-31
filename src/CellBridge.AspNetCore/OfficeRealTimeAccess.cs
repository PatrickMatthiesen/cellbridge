using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CellBridge.AspNetCore;

internal sealed class OfficeRealTimeAccess(CellBridgeDocumentService documents,
    IDataProtectionProvider protection, TimeProvider time, ProtocolRequestAdmission requests,
    ILogger<OfficeRealTimeAccess>? logger = null)
    : IOfficeRealTimePropertiesProvider, IOfficeRealTimeAuthorizer
{
    private static readonly string[] Names =
        ["RealtimeTypingEndpointUrl", "DocumentAccessToken", "DocumentAccessTokenTtl", "SharedStatus"];
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(20);
    public IReadOnlyCollection<string> PropertyNames => Names;

    public async ValueTask<IReadOnlyDictionary<string, string>> GetPropertiesAsync(Guid resourceId,
        CellBridgeActor actor, string publicBase, CancellationToken cancellationToken)
    {
        var admission = await documents.AuthorizeOfficeRealTimeAsync(resourceId, null, actor, null,
            DocumentAccess.Read, false, cancellationToken) ?? throw new UnauthorizedAccessException();
        var source = DocumentRequestResolver.CanonicalUrl(admission.Control.Head, publicBase);
        var expires = time.GetUtcNow().Add(TokenLifetime).ToUnixTimeMilliseconds();
        var data = new DocumentToken(resourceId, admission.Control.Head.LifecycleGeneration,
            actor.Identity, actor.AccessLimit, expires, source);
        var token = Protector(publicBase).Protect(JsonSerializer.Serialize(data));
        logger?.LogInformation("Office RTC ticket issued for {ResourceId} at lifecycle {LifecycleGeneration}; expires {ExpiresUtc}.",
            resourceId, admission.Control.Head.LifecycleGeneration, DateTimeOffset.FromUnixTimeMilliseconds(expires));
        var signature = QueryString.Create(new Dictionary<string, string?>
        {
            ["WopiSrc"] = source,
            ["access_token"] = token,
            ["access_token_ttl"] = expires.ToString(CultureInfo.InvariantCulture),
        }).Value![1..];
        var shared = admission.Control.Head.Security.Grants.Any(grant =>
            grant.Key != admission.Control.Head.Security.Owner && grant.Value != DocumentAccess.None);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RealtimeTypingEndpointUrl"] = publicBase.TrimEnd('/') + "/_cellbridge/rtc/",
            ["DocumentAccessToken"] = signature,
            ["DocumentAccessTokenTtl"] = expires.ToString(CultureInfo.InvariantCulture),
            ["SharedStatus"] = shared ? "Shared" : "Private",
        };
    }

    internal bool TryReadTicket(HttpContext context, out OfficeRealTimeTicket? ticket)
    {
        ticket = null;
        var publicBase = PublicBase(context);
        if (publicBase is null || context.Request.QueryString.Value?.Length > 24 * 1024 ||
            !Single(context.Request.Query, "qs", 16 * 1024, out var signature) ||
            !Single(context.Request.Query, "ClientId", 38, out var clientText) ||
            !Guid.TryParse(clientText, out var clientId) || clientId == Guid.Empty)
            return false;
        try
        {
            var fields = QueryHelpers.ParseQuery(signature);
            if (!Single(fields, "access_token", 12 * 1024, out var token) ||
                !Single(fields, "WopiSrc", 4096, out var source)) return false;
            var data = JsonSerializer.Deserialize<DocumentToken>(Protector(publicBase).Unprotect(token));
            if (data is null || data.Identity is null || data.ResourceId == Guid.Empty || data.LifecycleGeneration <= 0 ||
                data.Source != source || string.IsNullOrWhiteSpace(data.Identity.Subject) ||
                data.Identity.Subject.Length > 1024 || data.Identity.Login is null || data.Identity.Login.Length > 1024 ||
                data.Identity.DisplayName is null || data.Identity.DisplayName.Length > 1024 ||
                data.Identity.EmailAddress is { Length: > 1024 } ||
                data.Identity.SipAddress is { Length: > 1024 } ||
                data.AccessLimit is { } limit && (limit.ResourceId != data.ResourceId ||
                    (limit.Access & ~(DocumentAccess.Read | DocumentAccess.Write)) != 0)) return false;
            var expires = DateTimeOffset.FromUnixTimeMilliseconds(data.ExpiresUnixMilliseconds);
            if (expires <= time.GetUtcNow() || expires > time.GetUtcNow().Add(TokenLifetime).AddMinutes(1)) return false;
            ticket = new(data.ResourceId, data.LifecycleGeneration,
                new CellBridgeActor(data.Identity) { AccessLimit = data.AccessLimit }, clientId, expires);
            return true;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException or FormatException)
        { return false; }
    }

    public async ValueTask<bool> AuthorizeAsync(OfficeRealTimeTicket ticket, DocumentAccess requiredAccess,
        bool requireOwnedClient, CancellationToken cancellationToken)
    {
        if (ticket.ExpiresUtc <= time.GetUtcNow()) return false;
        var admitted = await requests.AcquireAsync(ticket.Actor.Identity.Subject, cancellationToken);
        using var lease = admitted.Lease;
        if (lease is null) throw new OfficeRealTimeBusyException();
        var authorized = await documents.AuthorizeOfficeRealTimeAsync(ticket.ResourceId, ticket.LifecycleGeneration,
            ticket.Actor, ticket.ClientId, requiredAccess, requireOwnedClient, cancellationToken);
        return authorized is not null && ticket.ExpiresUtc > time.GetUtcNow();
    }

    public async ValueTask<bool[]> AuthorizeTogetherAsync(IReadOnlyList<OfficeRealTimeTicketCheck> checks,
        CancellationToken cancellationToken)
    {
        if (checks.Count is < 1 or > 33) throw new ArgumentOutOfRangeException(nameof(checks));
        var first = checks[0].Ticket;
        if (checks.Any(check => check.Ticket.ResourceId != first.ResourceId ||
            check.Ticket.LifecycleGeneration != first.LifecycleGeneration)) return new bool[checks.Count];
        var admitted = await requests.AcquireAsync(first.Actor.Identity.Subject, cancellationToken);
        using var lease = admitted.Lease;
        if (lease is null) throw new OfficeRealTimeBusyException();
        var allowed = await documents.AuthorizeOfficeRealTimeTogetherAsync(first.ResourceId, first.LifecycleGeneration,
            checks.Select(check => new OfficeRealTimeAccessCheck(check.Ticket.Actor, check.Ticket.ClientId,
                check.RequiredAccess, check.RequireOwnedClient)).ToArray(), cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        for (int i = 0; i < allowed.Length; i++) allowed[i] &= checks[i].Ticket.ExpiresUtc > now;
        return allowed;
    }

    internal static string? PublicBase(HttpContext context)
    {
        var origin = CellBridgePublicOrigin.Resolve(context);
        return origin is null ? null : origin.TrimEnd('/') + DocumentUrlPaths.ValidateMount(context.Request.PathBase.Value ?? "");
    }

    private IDataProtector Protector(string publicBase) =>
        protection.CreateProtector("CellBridge.OfficeRealTime.DocumentAccess.v1", publicBase);

    private static bool Single(IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> fields,
        string name, int maximumLength, out string value)
    {
        value = "";
        var matches = fields.Where(field => field.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Value.Count != 1 ||
            matches[0].Value[0] is not { Length: > 0 } text || text.Length > maximumLength) return false;
        value = text;
        return true;
    }

    private sealed record DocumentToken(Guid ResourceId, long LifecycleGeneration, SubjectIdentity Identity,
        DocumentAccessLimit? AccessLimit, long ExpiresUnixMilliseconds, string Source);
}

internal sealed class OfficeRealTimeAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, OfficeRealTimeAccess access)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!access.TryReadTicket(Context, out var ticket) || ticket is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid Office real-time document ticket."));
        Context.Items[OfficeRealTimeRequest.TicketKey] = ticket;
        var identity = new ClaimsIdentity([
            new Claim(CellBridgeActor.SubjectClaim, ticket.Actor.Identity.Subject),
            new Claim(ClaimTypes.Name, ticket.Actor.Identity.Login),
            new Claim(CellBridgeActor.DisplayNameClaim, ticket.Actor.Identity.DisplayName),
        ], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
