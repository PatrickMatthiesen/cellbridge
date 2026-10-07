using System.Text.Encodings.Web;

namespace CellBridge.AspNetCore;

/// <summary>The default script-free HTML login page, usable in desktop Office.</summary>
public static class CellBridgeLoginPage
{
    public static string Render(CellBridgeLoginPageContext page)
    {
        var encode = HtmlEncoder.Default;
        var applicationName = encode.Encode(page.ApplicationName);
        var fields = page.RequiresTwoFactor ? $$"""
            <input type="hidden" name="{{CellBridgeLogin.MethodField}}" value="{{(page.UseRecoveryCode ? "recovery" : "authenticator")}}">
            <div class="field">
              <label for="code">{{(page.UseRecoveryCode ? "Recovery code" : "Authenticator code")}}</label>
              <input id="code" name="code" autocomplete="one-time-code" maxlength="256" required>
            </div>
            """ : """
            <div class="field">
              <label for="login">Username</label>
              <input id="login" name="login" autocomplete="username" autocapitalize="none" spellcheck="false" maxlength="256" required>
            </div>
            <div class="field">
              <label for="password">Password</label>
              <input id="password" name="password" type="password" autocomplete="current-password" maxlength="1024" required>
            </div>
            """;
        var continuation = page.RequiresTwoFactor ? $$"""
            <p class="alternate"><a href="{{encode.Encode(page.FormAction)}}?method={{(page.UseRecoveryCode ? "authenticator" : "recovery")}}">{{(page.UseRecoveryCode ? "Use an authenticator code" : "Use a recovery code")}}</a></p>
            <form method="post" action="{{encode.Encode(page.RestartAction!)}}" novalidate>
              <input type="hidden" name="{{encode.Encode(page.Antiforgery.FormFieldName)}}" value="{{encode.Encode(page.Antiforgery.RequestToken!)}}">
              <input type="hidden" name="{{CellBridgeLogin.StateField}}" value="{{encode.Encode(page.ProtectedState!)}}">
              <button class="restart" name="cancel" value="1" type="submit">Start over</button>
            </form>
            """ : "";
        // Keep styles local and the form script-free for Office's embedded sign-in window.
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta http-equiv="X-UA-Compatible" content="IE=edge">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Sign in to {{applicationName}}</title>
              <style>
                * { box-sizing: border-box; }
                html { background: #f4f8f8; }
                body { margin: 0; color: #24353b; font: 18px/1.5 "Segoe UI", Arial, sans-serif; }
                .sign-in { display: block; width: 100%; max-width: 520px; margin: 28px auto; padding: 32px; background: #fff; border: 1px solid #dce8e7; border-top: 4px solid #087f83; border-radius: 8px; }
                .eyebrow { margin: 0 0 10px; color: #536e73; font-size: 12px; font-weight: 600; letter-spacing: 1px; }
                h1 { margin: 0 0 10px; font-size: 28px; font-weight: 600; line-height: 1.2; }
                .intro { margin: 0 0 26px; color: #61777d; font-size: 15px; }
                label { display: block; margin-bottom: 7px; font-size: 16px; font-weight: 600; }
                .field { margin-bottom: 18px; }
                input { display: block; width: 100%; min-height: 48px; padding: 11px 12px; border: 1px solid #a9bfc2; border-radius: 5px; background: #fff; color: #24353b; font: inherit; }
                input:focus { outline: 0; border: 2px solid #087f83; padding: 10px 11px; box-shadow: 0 0 0 2px #dcefed; }
                input[type="hidden"] { display: none; }
                button { display: block; width: 100%; min-height: 48px; margin-top: 24px; padding: 11px 16px; border: 1px solid #087f83; border-radius: 5px; background: #087f83; color: #fff; font: inherit; font-weight: 600; cursor: pointer; }
                button:hover { background: #065f63; }
                button:focus { outline: 2px solid #065f63; outline-offset: 3px; }
                .error { margin: 0 0 22px; padding: 12px 14px; border-left: 3px solid #b34c2b; background: #fbe9df; color: #7c301a; font-size: 14px; }
                .note { margin: 26px 0 0; padding-top: 18px; border-top: 1px solid #dce8e7; color: #61777d; font-size: 14px; }
                .alternate { font-size: 15px; margin: 18px 0 0; }
                a { color: #065f63; }
                button.restart { background: #fff; color: #065f63; margin-top: 16px; }
                @media (max-width: 520px) { .sign-in { width: auto; margin: 18px; padding: 24px; } h1 { font-size: 25px; } }
                @media (max-height: 680px) { .sign-in { margin-top: 20px; margin-bottom: 20px; padding: 26px; } .intro { margin-bottom: 18px; } .field { margin-bottom: 14px; } .error { margin-bottom: 16px; padding: 10px 12px; } .note { margin-top: 20px; padding-top: 14px; } }
                {{(page.IsOffice ? "html, body { background: #fff; } .sign-in { max-width: none; width: auto; margin: 0; padding: 28px 32px; border: 0; border-radius: 0; } @media (max-width: 520px) { .sign-in { margin: 0; padding: 24px; } }" : "")}}
              </style>
            </head>
            <body>
              <!-- Use a known block element in Office's legacy embedded sign-in browser. -->
              <div class="sign-in" role="main">
                <p class="eyebrow">DOCUMENT WORKSPACE</p>
                <h1>{{(page.RequiresTwoFactor ? "Verify your sign-in" : "Sign in to " + applicationName)}}</h1>
                <p class="intro">{{(page.RequiresTwoFactor ? page.UseRecoveryCode ? "Enter one of your unused recovery codes." : "Enter the code from your authenticator app." : "Use your account to open and edit documents.")}}</p>
                {{(page.SignInFailed ? "<p class=\"error\" role=\"alert\">Sign-in failed. " + (page.RequiresTwoFactor ? "Check your code or start over." : "Check your credentials or try again later.") + "</p>" : "")}}
                <form method="post" action="{{encode.Encode(page.FormAction)}}" novalidate>
                  <input type="hidden" name="{{encode.Encode(page.Antiforgery.FormFieldName)}}" value="{{encode.Encode(page.Antiforgery.RequestToken!)}}">
                  <input type="hidden" name="{{encode.Encode(page.ReturnUrlParameter)}}" value="{{encode.Encode(page.ReturnUrl)}}">
                  {{(page.ProtectedState is null ? "" : "<input type=\"hidden\" name=\"" + CellBridgeLogin.StateField + "\" value=\"" + encode.Encode(page.ProtectedState) + "\">")}}
                  <input type="hidden" name="{{CellBridgeLogin.PresentationField}}" value="{{(page.IsOffice ? "office" : "browser")}}">
                  {{fields}}
                  <button type="submit">{{(page.RequiresTwoFactor ? "Verify and sign in" : "Sign in")}}</button>
                </form>
                {{continuation}}
                <p class="note">Document access depends on your account permissions.</p>
              </div>
            </body>
            </html>
            """;
    }
}
