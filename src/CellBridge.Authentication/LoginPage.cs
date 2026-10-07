using CellBridge.AspNetCore;

namespace CellBridge.Authentication;

internal static class LoginPage
{
    public static string Render(LoginPageContext page) => CellBridgeLoginPage.Render(new(
        page.ApplicationName, page.Antiforgery, page.ReturnUrl, page.SignInFailed,
        "/auth/login", "returnUrl"));
}
