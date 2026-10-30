using CellBridge.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CellBridge.TestInfrastructure;

/// <summary>Prepares real Identity tickets for document load tests, outside login throughput measurements.</summary>
internal sealed class LoadIdentitySessions(IHost host, string cookieName, string cookiePath, string[] tickets)
    : IAsyncDisposable
{
    public string CookieName { get; } = cookieName;
    public string CookiePath { get; } = cookiePath;
    public string this[int userIndex] => tickets[userIndex];

    public static async Task<LoadIdentitySessions> CreateAsync(string connectionString, Uri origin,
        IConfiguration sharedConfiguration, int userCount, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            DisableDefaults = true,
        });
        builder.Configuration.AddConfiguration(sharedConfiguration).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:cellbridge"] = connectionString,
            ["Authentication:PublicOrigin"] = origin.GetLeftPart(UriPartial.Authority),
        });
        builder.Services.AddCellBridgeAuthentication(builder.Configuration);
        var host = builder.Build();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<CellBridgeUser>>();
            var claims = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<CellBridgeUser>>();
            var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(IdentityConstants.ApplicationScheme);
            var tickets = new string[userCount];
            for (var index = 0; index < tickets.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var login = index == 0 ? "integration-writer" :
                    "integration-writer-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var user = await users.FindByIdAsync(login)
                    ?? throw new InvalidOperationException("The load-test account was not seeded.");
                if (!user.Enabled || await users.IsLockedOutAsync(user))
                    throw new InvalidOperationException("The load-test account cannot sign in.");
                var principal = await claims.CreateAsync(user);
                var now = DateTimeOffset.UtcNow;
                var properties = new AuthenticationProperties
                {
                    IssuedUtc = now,
                    ExpiresUtc = now.Add(options.ExpireTimeSpan),
                    IsPersistent = false,
                };
                tickets[index] = options.TicketDataFormat.Protect(
                    new AuthenticationTicket(principal, properties, IdentityConstants.ApplicationScheme));
            }
            return new(host, options.Cookie.Name!, options.Cookie.Path ?? "/", tickets);
        }
        catch
        {
            if (host is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
            else host.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (host is IAsyncDisposable asynchronous) return asynchronous.DisposeAsync();
        host.Dispose();
        return ValueTask.CompletedTask;
    }
}
