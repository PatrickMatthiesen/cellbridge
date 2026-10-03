#nullable enable
using System.Net;
using System.Text.RegularExpressions;

namespace CellBridge.Interop.Tests;

public sealed class LibraryAuthenticationTests
{
    [LiveInteropFact]
    public async Task SharedOriginLibraryShowsCallerAndForwardsCreationCsrf()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        using var http = LiveInteropHttp.Create(endpoint);
        using var page = await http.GetAsync("/library");
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Signed in as", html);
        Assert.Contains("/auth/logout", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        http.DefaultRequestHeaders.Remove("X-CellBridge-CSRF");
        var name = "library-" + Guid.NewGuid().ToString("N");
        // The Razor token is issued by demo; web validates it against the same
        // identity/key ring when the per-request catalog client forwards it.
        using var created = await http.PostAsync("/library?handler=Create", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["NewName"] = name, ["NewType"] = "docx", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var download = await http.GetAsync("/shared/" + name + ".docx");
        download.EnsureSuccessStatusCode();
    }
}
