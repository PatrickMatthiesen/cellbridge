using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CellBridge.AspNetCore;

/// <summary>The account types allowed by the Azure application registration.</summary>
public enum CellBridgeEntraAccountAudience
{
    SingleTenant,
    Organizations,
    OrganizationsAndPersonal,
    Personal,
}

/// <summary>A successfully read account policy. Tenant classification uses the validated token issuer.</summary>
public sealed record CellBridgeEntraAudienceSnapshot(
    CellBridgeEntraAccountAudience Audience, Guid HomeTenantId, DateTimeOffset RetrievedAt)
{
    public static readonly Guid PersonalTenantId = new("9188040d-6c67-4c5b-b112-36a304b66dad");

    public string AuthorityTenant => Audience switch
    {
        CellBridgeEntraAccountAudience.SingleTenant => HomeTenantId.ToString("D"),
        CellBridgeEntraAccountAudience.Organizations => "organizations",
        CellBridgeEntraAccountAudience.OrganizationsAndPersonal => "common",
        CellBridgeEntraAccountAudience.Personal => "consumers",
        _ => throw new InvalidOperationException("Unknown Entra account audience."),
    };

    public bool Allows(Guid issuingTenant) => issuingTenant != Guid.Empty && Audience switch
    {
        CellBridgeEntraAccountAudience.SingleTenant => issuingTenant == HomeTenantId,
        CellBridgeEntraAccountAudience.Organizations => issuingTenant != PersonalTenantId,
        CellBridgeEntraAccountAudience.OrganizationsAndPersonal => true,
        CellBridgeEntraAccountAudience.Personal => issuingTenant == PersonalTenantId,
        _ => false,
    };
}

/// <summary>Returns a current policy or throws when a current policy cannot be obtained.</summary>
public interface ICellBridgeEntraAudienceProvider
{
    ValueTask<CellBridgeEntraAudienceSnapshot> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>A safe discovery failure. Provider response bodies and credentials are never included.</summary>
public sealed class EntraAudienceUnavailableException : Exception
{
    public string ErrorCode { get; }
    public TimeSpan? RetryAfter { get; }

    public EntraAudienceUnavailableException(string errorCode, TimeSpan? retryAfter = null)
        : base("Entra application account policy is unavailable (" + errorCode + ").")
    {
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
    }
}

public sealed record CellBridgeEntraRegistrationMetadata(Guid ClientId, string SignInAudience, int? RequestedAccessTokenVersion);

/// <summary>Reads the application's own registration. Implementations must not return unverified local audience settings.</summary>
public interface ICellBridgeEntraRegistrationSource
{
    Task<CellBridgeEntraRegistrationMetadata> ReadAsync(CancellationToken cancellationToken);
}

internal static class CellBridgeEntraAudienceRegistration
{
    internal const string HttpClientName = "CellBridge.Entra.Discovery";

    internal static void Add(IServiceCollection services, CellBridgeEntraOptions options, Guid homeTenantId, Guid clientId)
    {
        if (homeTenantId == Guid.Empty || clientId == Guid.Empty)
            throw new ArgumentException("Entra discovery requires nonempty home tenant and application IDs.");
        if (options.AudienceRefreshInterval < TimeSpan.FromSeconds(1) || options.AudienceRefreshInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentException("AudienceRefreshInterval must be between one second and five minutes.");
        if (options.DiscoveryTimeout < TimeSpan.FromSeconds(1) || options.DiscoveryTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentException("DiscoveryTimeout must be between one and thirty seconds.");
        services.TryAddSingleton(TimeProvider.System);
        if (!options.DiscoverAccountTypes)
        {
            services.TryAddSingleton<ICellBridgeEntraAudienceProvider>(sp => new FixedEntraAudienceProvider(
                homeTenantId, sp.GetRequiredService<TimeProvider>()));
            return;
        }
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
            throw new ArgumentException("ClientSecret is required to discover the Azure application's account types.");
        var settings = new EntraDiscoverySettings(homeTenantId, clientId, options.ClientSecret,
            options.AudienceRefreshInterval, options.DiscoveryTimeout);
        services.AddHttpClient(HttpClientName, client => client.Timeout = settings.Timeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.TryAddSingleton<ICellBridgeEntraRegistrationSource>(sp => new GraphEntraRegistrationSource(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<TimeProvider>(), settings));
        services.TryAddSingleton<ICellBridgeEntraAudienceProvider>(sp => new CachedEntraAudienceProvider(
            sp.GetRequiredService<ICellBridgeEntraRegistrationSource>(), sp.GetRequiredService<TimeProvider>(), settings));
    }
}

// A class deliberately avoids record-generated formatting of the client credential.
internal sealed class EntraDiscoverySettings(Guid homeTenantId, Guid clientId, string clientSecret,
    TimeSpan interval, TimeSpan timeout)
{
    public Guid HomeTenantId { get; } = homeTenantId;
    public Guid ClientId { get; } = clientId;
    public string ClientSecret { get; } = clientSecret;
    public TimeSpan Interval { get; } = interval;
    public TimeSpan Timeout { get; } = timeout;
}

internal sealed class FixedEntraAudienceProvider(Guid homeTenantId, TimeProvider timeProvider) : ICellBridgeEntraAudienceProvider
{
    public ValueTask<CellBridgeEntraAudienceSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new CellBridgeEntraAudienceSnapshot(
            CellBridgeEntraAccountAudience.SingleTenant, homeTenantId, timeProvider.GetUtcNow()));
    }
}

internal sealed class CachedEntraAudienceProvider(ICellBridgeEntraRegistrationSource source, TimeProvider timeProvider,
    EntraDiscoverySettings settings) : ICellBridgeEntraAudienceProvider
{
    private readonly object _gate = new();
    private CellBridgeEntraAudienceSnapshot? _snapshot;
    private Task<CellBridgeEntraAudienceSnapshot>? _refresh;
    private EntraAudienceUnavailableException? _failure;
    private DateTimeOffset _retryAt;
    private int _failures;

    public ValueTask<CellBridgeEntraAudienceSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<CellBridgeEntraAudienceSnapshot> refresh;
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (_snapshot is { } snapshot && now < snapshot.RetrievedAt + settings.Interval)
                return ValueTask.FromResult(snapshot);
            if (_failure is not null && now < _retryAt)
                return ValueTask.FromException<CellBridgeEntraAudienceSnapshot>(_failure);
            if (_refresh is null || _refresh.IsCompleted) _refresh = RefreshAsync();
            refresh = _refresh;
        }
        // The refresh is shared. Cancelling a browser request cancels only that waiter.
        return new(refresh.WaitAsync(cancellationToken));
    }

    private async Task<CellBridgeEntraAudienceSnapshot> RefreshAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(settings.Timeout, timeProvider);
            var registration = await source.ReadAsync(timeout.Token).WaitAsync(settings.Timeout, timeProvider);
            if (registration.ClientId != settings.ClientId)
                throw new EntraAudienceUnavailableException("wrong-application");
            if (registration.RequestedAccessTokenVersion != 2)
                throw new EntraAudienceUnavailableException("unsupported-token-version");
            var audience = registration.SignInAudience switch
            {
                "AzureADMyOrg" => CellBridgeEntraAccountAudience.SingleTenant,
                "AzureADMultipleOrgs" => CellBridgeEntraAccountAudience.Organizations,
                "AzureADandPersonalMicrosoftAccount" => CellBridgeEntraAccountAudience.OrganizationsAndPersonal,
                "PersonalMicrosoftAccount" => CellBridgeEntraAccountAudience.Personal,
                _ => throw new EntraAudienceUnavailableException("unknown-audience"),
            };
            var snapshot = new CellBridgeEntraAudienceSnapshot(audience, settings.HomeTenantId, timeProvider.GetUtcNow());
            lock (_gate)
            {
                _snapshot = snapshot;
                _failure = null;
                _failures = 0;
            }
            return snapshot;
        }
        catch (Exception exception)
        {
            var failure = exception as EntraAudienceUnavailableException ?? new EntraAudienceUnavailableException(
                exception is OperationCanceledException or TimeoutException ? "discovery-timeout" : "discovery-failed");
            lock (_gate)
            {
                _failures = Math.Min(_failures + 1, 6);
                var backoff = failure.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(30, 1 << (_failures - 1)));
                backoff = TimeSpan.FromSeconds(Math.Clamp(backoff.TotalSeconds, 1, 300));
                _retryAt = timeProvider.GetUtcNow() + backoff;
                _failure = new EntraAudienceUnavailableException(failure.ErrorCode, backoff);
                failure = _failure;
            }
            throw failure;
        }
    }
}

internal sealed class GraphEntraRegistrationSource(IHttpClientFactory clients, TimeProvider timeProvider,
    EntraDiscoverySettings settings) : ICellBridgeEntraRegistrationSource
{
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public async Task<CellBridgeEntraRegistrationMetadata> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(settings.Timeout, timeProvider);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        cancellationToken = operation.Token;
        try
        {
            var token = await GetTokenAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/applications(appId='{settings.ClientId:D}')?$select=appId,signInAudience,api");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var client = clients.CreateClient(CellBridgeEntraAudienceRegistration.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await _tokenGate.WaitAsync(cancellationToken);
                try { if (_token == token) { _token = null; _tokenExpires = default; } }
                finally { _tokenGate.Release(); }
            }
            CheckStatus(response, tokenEndpoint: false);
            using var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            var application = Property(root, "appId");
            var audience = Property(root, "signInAudience");
            var api = Property(root, "api");
            var version = Property(api, "requestedAccessTokenVersion");
            if (application.ValueKind != JsonValueKind.String || !Guid.TryParse(application.GetString(), out var clientId) ||
                audience.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(audience.GetString()) ||
                version.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
                throw new EntraAudienceUnavailableException("invalid-response");
            int? tokenVersion = version.ValueKind == JsonValueKind.Null ? null
                : version.TryGetInt32(out var parsedVersion) ? parsedVersion
                : throw new EntraAudienceUnavailableException("invalid-response");
            return new(clientId, audience.GetString()!, tokenVersion);
        }
        catch (EntraAudienceUnavailableException) { throw; }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or OperationCanceledException or InvalidOperationException or FormatException or IOException)
        {
            throw new EntraAudienceUnavailableException(exception is OperationCanceledException ? "discovery-timeout" : "invalid-response");
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && timeProvider.GetUtcNow() < _tokenExpires) return _token;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://login.microsoftonline.com/{settings.HomeTenantId:D}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = settings.ClientId.ToString("D"),
                    ["client_secret"] = settings.ClientSecret,
                    ["grant_type"] = "client_credentials",
                    ["scope"] = "https://graph.microsoft.com/.default",
                }),
            };
            using var client = clients.CreateClient(CellBridgeEntraAudienceRegistration.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            CheckStatus(response, tokenEndpoint: true);
            using var document = await ReadJsonAsync(response, cancellationToken);
            var accessToken = Property(document.RootElement, "access_token");
            var expires = Property(document.RootElement, "expires_in");
            if (accessToken.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(accessToken.GetString()) ||
                expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt32(out var seconds) || seconds <= 0 || seconds > 86400)
                throw new EntraAudienceUnavailableException("invalid-response");
            _token = accessToken.GetString()!;
            // Short token lifetimes remain usable for the current request but are not cached.
            _tokenExpires = timeProvider.GetUtcNow().AddSeconds(Math.Max(0, seconds - 60));
            return _token;
        }
        finally { _tokenGate.Release(); }
    }

    private void CheckStatus(HttpResponseMessage response, bool tokenEndpoint)
    {
        if (response.IsSuccessStatusCode) return;
        var code = response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => "discovery-throttled",
            HttpStatusCode.Unauthorized => tokenEndpoint ? "credentials-rejected" : "graph-unauthorized",
            HttpStatusCode.Forbidden => "graph-forbidden",
            HttpStatusCode.BadRequest when tokenEndpoint => "credentials-rejected",
            HttpStatusCode.NotFound => "application-not-found",
            _ => "discovery-http-failure",
        };
        TimeSpan? retryAfter = null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter;
            retryAfter = retry?.Delta ?? (retry?.Date is { } date ? date - timeProvider.GetUtcNow() : TimeSpan.FromSeconds(1));
            retryAfter = TimeSpan.FromSeconds(Math.Clamp(retryAfter.Value.TotalSeconds, 1, 300));
        }
        throw new EntraAudienceUnavailableException(code, retryAfter);
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new EntraAudienceUnavailableException("invalid-response");
        var matches = value.EnumerateObject().Where(p => p.NameEquals(name)).ToArray();
        if (matches.Length != 1) throw new EntraAudienceUnavailableException("invalid-response");
        return matches[0].Value;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new EntraAudienceUnavailableException("response-too-large");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
                throw new EntraAudienceUnavailableException("response-too-large");
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }
}
