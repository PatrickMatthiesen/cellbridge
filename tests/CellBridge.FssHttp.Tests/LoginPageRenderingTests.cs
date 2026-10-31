using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;

namespace CellBridge.FssHttp.Tests;

public sealed class LoginPageRenderingTests
{
    [Fact]
    public void ExternalSignInRendersInsideCardBeforeLocalFormAndEncodesValues()
    {
        var html = CellBridgeLoginPage.Render(Page() with
        {
            ExternalSignIn = new CellBridgeLoginExternalAction(
                "Sign in with Microsoft <script>",
                "/auth/entra?returnUrl=%2Flibrary%3Fview%3Done%26sort%3Dname")
        });

        var cardStart = html.IndexOf("<div class=\"sign-in\"", StringComparison.Ordinal);
        var action = html.IndexOf("class=\"external-sign-in-action\"", StringComparison.Ordinal);
        var separator = html.IndexOf("Or use a local account", StringComparison.Ordinal);
        var localForm = html.IndexOf("<form method=\"post\"", StringComparison.Ordinal);
        var cardEnd = html.LastIndexOf("</div>", StringComparison.Ordinal);

        Assert.True(cardStart >= 0 && cardStart < action);
        Assert.True(action < separator && separator < localForm && localForm < cardEnd);
        Assert.Contains("Sign in with Microsoft &lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("href=\"/auth/entra?returnUrl=%2Flibrary%3Fview%3Done%26sort%3Dname\"", html);
        Assert.Contains("name=\"__RequestVerificationToken\" value=\"csrf-token\"", html);
        Assert.Contains("name=\"returnUrl\" value=\"/library?view=one&amp;sort=name\"", html);
        Assert.Contains("autocomplete=\"username\"", html);
        Assert.Contains("autocomplete=\"current-password\"", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalSignInIsOmittedFromTwoFactorAndOfficePages(bool isOffice)
    {
        var html = CellBridgeLoginPage.Render(Page() with
        {
            IsOffice = isOffice,
            RequiresTwoFactor = !isOffice,
            ProtectedState = "protected-login-state",
            RestartAction = "/auth/login/restart",
            ExternalSignIn = new CellBridgeLoginExternalAction("Sign in with Microsoft", "/auth/entra")
        });

        Assert.DoesNotContain("class=\"external-sign-in-action\"", html, StringComparison.Ordinal);
        if (isOffice)
        {
            Assert.Contains("name=\"login\"", html);
            Assert.Contains("name=\"password\"", html);
        }
        else
        {
            Assert.Contains("name=\"code\"", html);
            Assert.DoesNotContain("name=\"login\"", html);
        }
    }

    private static CellBridgeLoginPageContext Page() => new(
        "CellBridge",
        new AntiforgeryTokenSet("csrf-token", "csrf-cookie", "__RequestVerificationToken", null),
        "/library?view=one&sort=name",
        false,
        "/auth/login",
        "returnUrl");
}
