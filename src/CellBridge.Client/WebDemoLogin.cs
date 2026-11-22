using System.Net;
using System.Text.RegularExpressions;

namespace CellBridge.Client;

/// <summary>Signs in to WebDemo with the same antiforgery form as its browser login.</summary>
public static partial class WebDemoLogin
{
    /// <summary>Uses a caller-owned HTTP client configured with cookies.</summary>
    public static async Task SignInAsync(HttpClient client, Uri origin, string login, string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var loginPage = new Uri(origin, "/auth/login");
        var complete = new Uri(origin, "/auth/complete");
        var html = await client.GetStringAsync(loginPage, cancellationToken).ConfigureAwait(false);
        var match = AntiforgeryToken().Match(html);
        if (!match.Success)
            throw new InvalidDataException("The WebDemo login page did not issue an antiforgery token.");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = login,
            ["password"] = password,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
            ["returnUrl"] = complete.AbsolutePath,
        });
        using var response = await client.PostAsync(loginPage, form, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && response.RequestMessage?.RequestUri == complete)
            return;
        if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther &&
            response.Headers.Location is { } location && new Uri(loginPage, location) == complete)
        {
            using var check = await client.GetAsync(complete, cancellationToken).ConfigureAwait(false);
            if (check.IsSuccessStatusCode && check.RequestMessage?.RequestUri == complete)
                return;
        }
        throw new UnauthorizedAccessException($"WebDemo did not complete sign-in (HTTP {(int)response.StatusCode}).");
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryToken();
}
