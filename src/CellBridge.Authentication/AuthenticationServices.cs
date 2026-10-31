using System.Security.Claims;
using System.Threading.RateLimiting;
using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CellBridge.Authentication;

public static class AuthenticationServices
{
    public const string CookieName = "CellBridge.Authentication";
    public const string AntiforgeryCookie = "CellBridge.Antiforgery";
    public const string AntiforgeryHeader = "X-CellBridge-CSRF";
    public const string LoginRateLimitPolicy = "CellBridge.Authentication.Login";

    public static IServiceCollection AddCellBridgeAuthentication(this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("cellbridge")
            ?? throw new InvalidOperationException("Authentication requires an initialized PostgreSQL identity database.");
        var publicOrigin = configuration["Authentication:PublicOrigin"]
            ?? throw new InvalidOperationException("Set Authentication:PublicOrigin to the HTTPS origin reachable by Office.");
        if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new InvalidOperationException("Authentication:PublicOrigin must be an HTTPS origin without path or credentials.");
        services.AddSingleton(new AuthenticationOrigin(origin.GetLeftPart(UriPartial.Authority)));
        services.AddOptions<AuthenticationPageOptions>()
            .Bind(configuration.GetSection("Authentication"))
            .Configure(o => o.EntraLoginEnabled = configuration.GetValue<bool>("Authentication:Entra:Enabled"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApplicationName), "Authentication:ApplicationName must not be blank.")
            .ValidateOnStart();
        services.AddDbContextFactory<AuthenticationDatabase>(o => o.UseNpgsql(connectionString));
        services.AddIdentityCore<CellBridgeUser>(o =>
        {
            o.Password.RequiredLength = 12;
            o.Lockout.MaxFailedAccessAttempts = 5;
            o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            o.SignIn.RequireConfirmedAccount = true;
        }).AddEntityFrameworkStores<AuthenticationDatabase>().AddSignInManager()
            .AddClaimsPrincipalFactory<CellBridgeClaimsFactory>();
        services.AddScoped<IUserConfirmation<CellBridgeUser>, EnabledUserConfirmation>();
        services.AddAuthentication(o =>
        {
            o.DefaultScheme = IdentityConstants.ApplicationScheme;
            o.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
        }).AddIdentityCookies();
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.Path = "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            o.LoginPath = "/auth/login";
            o.Events.OnRedirectToLogin = context =>
            {
                var request = context.Request;
                if (request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else
                    context.Response.Redirect(origin.GetLeftPart(UriPartial.Authority) + "/auth/login?returnUrl=" +
                        Uri.EscapeDataString(request.PathBase + request.Path + request.QueryString));
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = context =>
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
        var entraEnabled = configuration.GetValue<bool>("Authentication:Entra:Enabled");
        EntraAuthentication.AddCellBridgeEntraAuthentication(services, configuration);
        services.AddCellBridgeOfficeFormsAuthentication(IdentityConstants.ApplicationScheme,
            o =>
            {
                o.PublicOrigin = origin.GetLeftPart(UriPartial.Authority);
                if (entraEnabled)
                {
                    o.ProtocolAuthenticationScheme = EntraAuthentication.OfficeScheme;
                }
            });
        services.AddAuthorization();
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = AntiforgeryCookie;
            o.Cookie.Path = "/";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.HeaderName = AntiforgeryHeader;
        });
        services.AddRequestTimeouts();
        services.AddSingleton<AuthenticationLoginRateLimitPolicy>();
        services.AddRateLimiter();
        services.AddOptions<RateLimiterOptions>()
            .Configure<AuthenticationLoginRateLimitPolicy>((options, policy) =>
                options.AddPolicy(LoginRateLimitPolicy, policy));
        var keyCertificate = AuthenticationKeyCertificate.Load(configuration);
        services.AddSingleton<AuthenticationKeyCertificate>(_ => keyCertificate);
        services.AddSingleton<AuthenticationKeyProtector>();
        services.AddHostedService<AuthenticationKeyValidationService>();
        services.AddDataProtection()
            .SetApplicationName("CellBridge.Authentication.v1")
            .ProtectKeysWithCertificate(keyCertificate.Certificate);
        services.AddSingleton<DatabaseKeyRepository>();
        services.AddOptions<KeyManagementOptions>().Configure<DatabaseKeyRepository>((o, repository) => o.XmlRepository = repository);
        return services;
    }


}

internal sealed class AuthenticationLoginRateLimitPolicy : IRateLimiterPolicy<string>, IDisposable
{
    private readonly RateLimiter _aggregate = CreateAggregateLimiter();

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } =
        static (context, _) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return ValueTask.CompletedTask;
        };

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        string address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.Get(address, _ => CreateRemoteAddressLimiter());
    }

    public void Dispose() => _aggregate.Dispose();

    private static RateLimiter CreateAggregateLimiter()
    {
        var window = new FixedWindowRateLimiter(new()
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
        var concurrency = new ConcurrencyLimiter(new()
        {
            PermitLimit = 8,
            QueueLimit = 0,
        });
        return new OwnedChainedRateLimiter([window, concurrency], [window, concurrency]);
    }

    private RateLimiter CreateRemoteAddressLimiter()
    {
        var window = new FixedWindowRateLimiter(new()
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
        var concurrency = new ConcurrencyLimiter(new()
        {
            PermitLimit = 2,
            QueueLimit = 0,
        });
        return new OwnedChainedRateLimiter(
            [window, concurrency, new NonDisposingRateLimiter(_aggregate)],
            [window, concurrency]);
    }

    private sealed class NonDisposingRateLimiter(RateLimiter inner) : RateLimiter
    {
        public override TimeSpan? IdleDuration => inner.IdleDuration;

        public override RateLimiterStatistics? GetStatistics() => inner.GetStatistics();

        protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
            inner.AttemptAcquire(permitCount);

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(
            int permitCount, CancellationToken cancellationToken) =>
            inner.AcquireAsync(permitCount, cancellationToken);

        protected override void Dispose(bool disposing)
        {
        }

        protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Wraps a framework chained limiter while retaining ownership of its local
/// components. Borrowed components do not affect partition idleness.
/// </summary>
internal sealed class OwnedChainedRateLimiter : RateLimiter
{
    private readonly RateLimiter _chain;
    private readonly RateLimiter[] _ownedLimiters;
    private bool _disposed;

    public OwnedChainedRateLimiter(RateLimiter[] limiters, RateLimiter[] ownedLimiters)
    {
        _chain = RateLimiter.CreateChained(limiters);
        _ownedLimiters = (RateLimiter[])ownedLimiters.Clone();
    }

    public override TimeSpan? IdleDuration
    {
        get
        {
            ThrowIfDisposed();
            TimeSpan? lowest = null;
            foreach (var limiter in _ownedLimiters)
            {
                if (limiter.IdleDuration is not { } idle) return null;
                if (lowest is null || idle < lowest) lowest = idle;
            }
            return lowest;
        }
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        ThrowIfDisposed();
        return _chain.GetStatistics();
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ThrowIfDisposed();
        return _chain.AttemptAcquire(permitCount);
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _chain.AcquireAsync(permitCount, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed) return;
        _disposed = true;

        List<Exception>? errors = null;
        Dispose(_chain, ref errors);
        foreach (var limiter in _ownedLimiters)
            Dispose(limiter, ref errors);
        if (errors is not null) throw new AggregateException(errors);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        Dispose(true);
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OwnedChainedRateLimiter));
    }

    private static void Dispose(RateLimiter limiter, ref List<Exception>? errors)
    {
        try { limiter.Dispose(); }
        catch (Exception error)
        {
            errors ??= [];
            errors.Add(error);
        }
    }
}

public sealed record AuthenticationOrigin(string Value);

internal sealed class EnabledUserConfirmation : IUserConfirmation<CellBridgeUser>
{
    public Task<bool> IsConfirmedAsync(UserManager<CellBridgeUser> manager, CellBridgeUser user) => Task.FromResult(user.Enabled);
}

internal sealed class CellBridgeClaimsFactory(UserManager<CellBridgeUser> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<CellBridgeUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(CellBridgeUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new(CellBridgeActor.SubjectClaim, user.Subject));
        identity.AddClaim(new(CellBridgeActor.DisplayNameClaim, user.DisplayName));
        if (user.CanCreate) identity.AddClaim(new(CellBridgeActor.CreateClaim, "true"));
        return identity;
    }
}
