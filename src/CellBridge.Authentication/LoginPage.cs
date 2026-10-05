using System.Text.Encodings.Web;

namespace CellBridge.Authentication;

internal static class LoginPage
{
    public static string Render(LoginPageContext page)
    {
        var encode = HtmlEncoder.Default;
        var applicationName = encode.Encode(page.ApplicationName);
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
                html { background: #f4f1e9; }
                body { margin: 0; color: #20231f; font: 18px/1.5 "Segoe UI", Arial, sans-serif; }
                .sign-in { display: block; width: 100%; max-width: 520px; margin: 0 auto; padding: 36px 24px; }
                h1 { margin: 0 0 26px; font-size: 32px; font-weight: 600; line-height: 1.2; letter-spacing: -.03em; }
                label { display: block; margin-bottom: 7px; font-size: 16px; font-weight: 600; }
                .field { margin-bottom: 18px; }
                input { display: block; width: 100%; min-height: 46px; padding: 11px 12px; border: 1px solid #aaa99e; border-radius: 6px; background: #fff; color: #20231f; font: inherit; }
                input:focus { outline: 2px solid #527c62; outline-offset: 2px; border-color: #527c62; }
                input[type="hidden"] { display: none; }
                button { display: block; width: 100%; min-height: 46px; margin-top: 24px; padding: 11px 16px; border: 1px solid #20231f; border-radius: 6px; background: #20231f; color: #f4f1e9; font: inherit; font-weight: 600; cursor: pointer; }
                button:hover { background: #3b453b; }
                button:focus { outline: 2px solid #527c62; outline-offset: 3px; }
                .error { margin: 0 0 22px; padding: 12px 14px; border-left: 3px solid #b34c2b; background: #fbe9df; color: #7c301a; font-size: 14px; }
                .note { margin: 26px 0 0; padding-top: 18px; border-top: 1px solid #d8d5ca; color: #5f655b; font-size: 14px; }
                @media (max-width: 360px) { .sign-in { padding: 24px 18px; } h1 { font-size: 28px; } }
              </style>
            </head>
            <body>
              <!-- Use a known block element in Office's legacy embedded sign-in browser. -->
              <div class="sign-in" role="main">
                <h1>Sign in to {{applicationName}}</h1>
                {{(page.SignInFailed ? "<p class=\"error\" role=\"alert\">Sign-in failed. Check your credentials or try again later.</p>" : "")}}
                <form method="post" action="/auth/login">
                  <input type="hidden" name="{{encode.Encode(page.Antiforgery.FormFieldName)}}" value="{{encode.Encode(page.Antiforgery.RequestToken!)}}">
                  <input type="hidden" name="returnUrl" value="{{encode.Encode(page.ReturnUrl)}}">
                  <div class="field">
                    <label for="login">Username</label>
                    <input id="login" name="login" autocomplete="username" autocapitalize="none" spellcheck="false" maxlength="256" required>
                  </div>
                  <div class="field">
                    <label for="password">Password</label>
                    <input id="password" name="password" type="password" autocomplete="current-password" maxlength="1024" required>
                  </div>
                  <button type="submit">Sign in</button>
                </form>
                <p class="note">Document access depends on your account permissions.</p>
              </div>
            </body>
            </html>
            """;
    }
}
