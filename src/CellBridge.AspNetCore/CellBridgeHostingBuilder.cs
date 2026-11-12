using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CellBridge.AspNetCore;

/// <summary>A provider identity produced after token validation. It is not a document account or permission grant.</summary>
public sealed record ValidatedExternalIdentity(string Provider, string ProviderKey, Guid TenantId, Guid ObjectId);

/// <summary>Maps a validated external login to an existing, permitted host account. Return null to reject sign-in.</summary>
/// <remarks>Implement all host account checks and return one authenticated identity with a stable CellBridge subject.
/// The host retains ongoing cookie account validation and must preserve the stable subject when refreshing its principal.
/// Do not link accounts by email, create accounts implicitly, or copy external permission claims.</remarks>
public interface ICellBridgeExternalIdentityMapper
{
    ValueTask<ClaimsPrincipal?> MapAsync(ValidatedExternalIdentity identity, CancellationToken cancellationToken);
}

/// <summary>Maps existing ASP.NET Core Identity external-login links without creating or linking accounts.</summary>
public sealed class CellBridgeExternalIdentityOptions<TUser> where TUser : class
{
    /// <summary>Stable namespace for default subjects, matching packaged Identity login. Do not change after creating documents.</summary>
    public string IdentityAuthority { get; set; } = "application";
    /// <summary>Optional explicit subject. Otherwise retain the host subject or derive one from IdentityAuthority and user ID.</summary>
    public Func<TUser, string>? Subject { get; set; }
    public Func<TUser, string?>? DisplayName { get; set; }
    public Func<TUser, bool>? CanCreate { get; set; }
    /// <summary>Additional host account policy, evaluated after Identity's sign-in and lockout checks.</summary>
    public Func<TUser, bool>? IsAllowed { get; set; }
}

/// <summary>Configures HTTP hosting features without introducing ASP.NET Core dependencies into the protocol options.</summary>
public sealed class CellBridgeHostingBuilder
{
    private readonly IServiceCollection _services;
    private readonly CellBridgeOptions _options;
    private bool _authenticationConfigured;
    internal CellBridgeHostingBuilder(IServiceCollection services, CellBridgeOptions options) => (_services, _options) = (services, options);

    public CellBridgeHostingBuilder Authentication(Action<CellBridgeAuthenticationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_authenticationConfigured) throw new InvalidOperationException("Configure CellBridge authentication once per host.");
        _authenticationConfigured = true;
        var builder = new CellBridgeAuthenticationBuilder(_services);
        configure(builder);
        builder.Register(_options);
        return this;
    }
}

/// <summary>Selects the native Office provider and a host-owned account mapper. Does not change host authentication defaults.</summary>
public sealed class CellBridgeAuthenticationBuilder
{
    private readonly IServiceCollection _services;
    private CellBridgeEntraOptions? _entra;
    private string? _cookieScheme;
    private bool _mapperSelected;
    internal CellBridgeAuthenticationBuilder(IServiceCollection services) => _services = services;

    public CellBridgeAuthenticationBuilder UseEntra(Action<CellBridgeEntraOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_entra is not null) throw new InvalidOperationException("Configure one native Office Entra provider per host.");
        _entra = new();
        configure(_entra);
        return this;
    }

    public CellBridgeAuthenticationBuilder UseEntra(IConfigurationSection configuration) => UseEntra(configuration.Bind);

    /// <summary>Uses the existing Identity application cookie and linked accounts. Requires the host's Identity services and stores.</summary>
    public CellBridgeAuthenticationBuilder UseIdentity<TUser>(Action<CellBridgeExternalIdentityOptions<TUser>>? configure = null)
        where TUser : class
    {
        SelectMapper(IdentityConstants.ApplicationScheme);
        var options = new CellBridgeExternalIdentityOptions<TUser>();
        configure?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.IdentityAuthority) || options.IdentityAuthority.Length > 256)
            throw new ArgumentException("IdentityAuthority must be a stable, non-empty namespace of at most 256 characters.");
        _services.AddSingleton(options);
        _services.TryAddScoped<ICellBridgeExternalIdentityMapper, IdentityExternalIdentityMapper<TUser>>();
        return this;
    }

    /// <summary>Uses a scoped host mapper and existing cookie scheme. The mapper owns account status and policy checks.</summary>
    public CellBridgeAuthenticationBuilder UseMapper<TMapper>(string cookieScheme = CookieAuthenticationDefaults.AuthenticationScheme)
        where TMapper : class, ICellBridgeExternalIdentityMapper
    {
        SelectMapper(cookieScheme);
        _services.Replace(ServiceDescriptor.Scoped<ICellBridgeExternalIdentityMapper, TMapper>());
        return this;
    }

    private void SelectMapper(string cookieScheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookieScheme);
        if (_mapperSelected) throw new InvalidOperationException("Select one external account mapper per host.");
        _mapperSelected = true;
        _cookieScheme = cookieScheme;
    }

    internal void Register(CellBridgeOptions options)
    {
        if (_entra is null) throw new InvalidOperationException("Select an authentication provider with UseEntra.");
        if (_cookieScheme is not null)
        {
            if (_entra.CookieScheme != IdentityConstants.ApplicationScheme && _entra.CookieScheme != _cookieScheme)
                throw new InvalidOperationException("The account mapper and Entra authentication must use the same cookie scheme.");
            _entra.CookieScheme = _cookieScheme;
        }
        EntraAuthentication.Register(_services, _entra, options);
    }
}

internal interface ICellBridgeExternalPrincipalRefresher
{
    ValueTask<ClaimsPrincipal?> RefreshAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}

internal sealed class IdentityExternalIdentityMapper<TUser>(UserManager<TUser> users, SignInManager<TUser> signIn,
    CellBridgeExternalIdentityOptions<TUser> options) : ICellBridgeExternalIdentityMapper, ICellBridgeExternalPrincipalRefresher where TUser : class
{
    public async ValueTask<ClaimsPrincipal?> MapAsync(ValidatedExternalIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await users.FindByLoginAsync(identity.Provider, identity.ProviderKey);
        if (user is null || !await CanSignInAsync(user)) return null;
        return await MapPrincipalAsync(user, await signIn.CreateUserPrincipalAsync(user));
    }

    public async ValueTask<ClaimsPrincipal?> RefreshAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await users.GetUserAsync(principal);
        if (user is null || !await CanSignInAsync(user)) return null;
        return await MapPrincipalAsync(user, principal);
    }

    // External sign-in and cookie refresh do not replace the host's local second-factor continuation.
    private async Task<bool> CanSignInAsync(TUser user) => await signIn.CanSignInAsync(user) &&
        !(users.SupportsUserLockout && await users.IsLockedOutAsync(user)) &&
        !(users.SupportsUserTwoFactor && await users.GetTwoFactorEnabledAsync(user)) && options.IsAllowed?.Invoke(user) != false;

    private async ValueTask<ClaimsPrincipal?> MapPrincipalAsync(TUser user, ClaimsPrincipal principal)
    {
        var authenticated = principal.Identities.Where(i => i.IsAuthenticated).ToArray();
        if (authenticated.Length != 1) return null;
        var target = authenticated[0];
        if (options.Subject is { } subjectSelector)
            Set(target, CellBridgeActor.SubjectClaim, subjectSelector(user));
        else if (!target.HasClaim(c => c.Type == CellBridgeActor.SubjectClaim))
        {
            var id = await users.GetUserIdAsync(user);
            if (string.IsNullOrWhiteSpace(id)) return null;
            var authority = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(options.IdentityAuthority));
            Set(target, CellBridgeActor.SubjectClaim, $"identity:{authority}:{WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(id))}");
        }
        if (options.DisplayName is { } display) Set(target, CellBridgeActor.DisplayNameClaim, display(user));
        else if (!target.HasClaim(c => c.Type == CellBridgeActor.DisplayNameClaim))
            Set(target, CellBridgeActor.DisplayNameClaim, await users.GetUserNameAsync(user));
        if (options.CanCreate is { } canCreate) Set(target, CellBridgeActor.CreateClaim, canCreate(user) ? "true" : "false");
        return EntraAuthentication.IsMappedPrincipal(principal) ? principal : null;
    }

    private static void Set(ClaimsIdentity identity, string type, string? value)
    {
        foreach (var claim in identity.FindAll(type).ToArray()) identity.RemoveClaim(claim);
        if (value is not null) identity.AddClaim(new(type, value));
    }
}
