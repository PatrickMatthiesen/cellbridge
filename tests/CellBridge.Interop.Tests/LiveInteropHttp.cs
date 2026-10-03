using System.Net.Security;

namespace CellBridge.Interop.Tests;

internal static class LiveInteropHttp
{
    public static HttpClient Create(Uri baseAddress = null)
    {
        var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        // Only the loopback developer certificate is accepted by the local runner.
        handler.ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            errors == SslPolicyErrors.None || request.RequestUri?.IsLoopback == true;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        if (baseAddress != null) client.BaseAddress = baseAddress;
        var cookie = Environment.GetEnvironmentVariable("CELLBRIDGE_INTEROP_COOKIE");
        var csrf = Environment.GetEnvironmentVariable("CELLBRIDGE_INTEROP_CSRF");
        if (string.IsNullOrWhiteSpace(cookie))
            throw new InvalidOperationException("Live tests require CELLBRIDGE_INTEROP_COOKIE from an ordinary authenticated login.");
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        if (!string.IsNullOrWhiteSpace(csrf)) client.DefaultRequestHeaders.Add("X-CellBridge-CSRF", csrf);
        return client;
    }
}
