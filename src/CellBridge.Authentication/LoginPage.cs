using CellBridge.AspNetCore;

namespace CellBridge.Authentication;

internal static class LoginPage
{
    public static string Render(LoginPageContext page, bool entraLoginEnabled)
    {
        var externalSignIn = entraLoginEnabled
            ? new CellBridgeLoginExternalAction("Sign in with Microsoft",
                "/auth/entra?returnUrl=" + Uri.EscapeDataString(page.ReturnUrl))
            : null;
        return CellBridgeLoginPage.Render(new(
            page.ApplicationName, page.Antiforgery, page.ReturnUrl, page.SignInFailed,
            "/auth/login", "returnUrl")
        {
            ExternalSignIn = externalSignIn
        });
    }
}
