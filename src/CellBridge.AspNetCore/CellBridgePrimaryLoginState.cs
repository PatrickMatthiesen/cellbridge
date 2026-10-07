using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.AspNetCore;

// Protect the initial Office destination as well as the later Identity continuation.
internal static class CellBridgePrimaryLoginState
{
    internal const int MaxTokenLength = 3000;
    internal sealed record State(string ReturnUrl, bool IsOffice, string PathBase, string CsrfHash, long Expires);

    internal static (string Token, string ReturnUrl) Create(HttpContext context,
        CellBridgeLogin.LoginRegistration login, CellBridgeOfficeFormsAuthentication.OfficeFormsRegistration office,
        string returnUrl, bool isOffice, string csrfToken)
    {
        var state = new State(returnUrl, isOffice, context.Request.PathBase.ToUriComponent(), Hash(csrfToken),
            Clock(context).GetUtcNow().AddMinutes(15).UtcTicks);
        var token = Protect(context, login, office, state);
        if (token.Length > MaxTokenLength && !isOffice)
        {
            state = state with { ReturnUrl = (context.Request.PathBase + login.Options.DefaultReturnPath).ToUriComponent() };
            token = Protect(context, login, office, state);
        }
        if (token.Length > MaxTokenLength)
            throw new InvalidOperationException("The configured mounted login destination is too long for protected sign-in state.");
        return (token, state.ReturnUrl);
    }

    internal static State? Read(HttpContext context, CellBridgeLogin.LoginRegistration login,
        CellBridgeOfficeFormsAuthentication.OfficeFormsRegistration office, string token, string csrfToken)
    {
        if (token.Length is 0 or > MaxTokenLength) return null;
        try
        {
            using var stream = new MemoryStream(Protector(context, login, office).Unprotect(WebEncoders.Base64UrlDecode(token)));
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            var state = new State(reader.ReadString(), reader.ReadBoolean(), reader.ReadString(), reader.ReadString(), reader.ReadInt64());
            if (stream.Position != stream.Length || state.PathBase != context.Request.PathBase.ToUriComponent() ||
                state.CsrfHash != Hash(csrfToken) || state.Expires <= Clock(context).GetUtcNow().UtcTicks ||
                CellBridgeLogin.LocalReturnUrl(state.ReturnUrl, "") != state.ReturnUrl ||
                state.IsOffice && state.ReturnUrl != (context.Request.PathBase + office.CompletionPath).ToUriComponent()) return null;
            return state;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or InvalidDataException or EndOfStreamException)
        { return null; }
    }

    private static string Protect(HttpContext context, CellBridgeLogin.LoginRegistration login,
        CellBridgeOfficeFormsAuthentication.OfficeFormsRegistration office, State state)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(state.ReturnUrl); writer.Write(state.IsOffice); writer.Write(state.PathBase);
            writer.Write(state.CsrfHash); writer.Write(state.Expires);
        }
        return WebEncoders.Base64UrlEncode(Protector(context, login, office).Protect(stream.ToArray()));
    }
    private static IDataProtector Protector(HttpContext context, CellBridgeLogin.LoginRegistration login,
        CellBridgeOfficeFormsAuthentication.OfficeFormsRegistration office) =>
        context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("CellBridge.Login", "primary-v1",
            office.CookieScheme, login.Options.OfficeLoginPath.Value!, office.CompletionPath.Value!);
    private static TimeProvider Clock(HttpContext context) => context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
    private static string Hash(string value) => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
