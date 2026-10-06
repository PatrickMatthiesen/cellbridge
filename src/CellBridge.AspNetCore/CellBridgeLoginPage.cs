using System.Text.Encodings.Web;

namespace CellBridge.AspNetCore;

/// <summary>The default script-free HTML login page, usable in desktop Office.</summary>
public static class CellBridgeLoginPage
{
    public static string Render(CellBridgeLoginPageContext page)
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
                html { background: #f3f5f8; }
                body { margin: 0; color: #202b3c; font: 18px/1.5 "Segoe UI", Arial, sans-serif; }
                .sign-in { display: block; width: 100%; max-width: 480px; margin: 32px auto; padding: 32px; background: #fff; border: 1px solid #dbe1ea; border-radius: 10px; }
                h1 { margin: 0 0 26px; font-size: 28px; font-weight: 600; line-height: 1.2;  }
                label { display: block; margin-bottom: 7px; font-size: 16px; font-weight: 600; }
                .field { margin-bottom: 18px; }
                input { display: block; width: 100%; min-height: 46px; padding: 11px 12px; border: 1px solid #a7b2c3; border-radius: 6px; background: #fff; color: #202b3c; font: inherit; }
                input:focus { outline: 2px solid #2457ce; outline-offset: 2px; border-color: #2457ce; }
                input[type="hidden"] { display: none; }
                button { display: block; width: 100%; min-height: 46px; margin-top: 24px; padding: 11px 16px; border: 1px solid #2457ce; border-radius: 6px; background: #2457ce; color: #fff; font: inherit; font-weight: 600; cursor: pointer; }
                button:hover { background: #1946b3; }
                button:focus { outline: 2px solid #2457ce; outline-offset: 3px; }
                .error { margin: 0 0 22px; padding: 12px 14px; border-left: 3px solid #b34c2b; background: #fbe9df; color: #7c301a; font-size: 14px; }
                .note { margin: 26px 0 0; padding-top: 18px; border-top: 1px solid #dbe1ea; color: #5c687a; font-size: 14px; }
                @media (max-width: 520px) { .sign-in { width: auto; margin: 18px; padding: 24px; } h1 { font-size: 25px; } }
              </style>
            </head>
            <body>
              <!-- Use a known block element in Office's legacy embedded sign-in browser. -->
              <div class="sign-in" role="main">
                <h1>Sign in to {{applicationName}}</h1>
                {{(page.SignInFailed ? "<p class=\"error\" role=\"alert\">Sign-in failed. Check your credentials or try again later.</p>" : "")}}
                <form method="post" action="{{encode.Encode(page.FormAction)}}">
                  <input type="hidden" name="{{encode.Encode(page.Antiforgery.FormFieldName)}}" value="{{encode.Encode(page.Antiforgery.RequestToken!)}}">
                  <input type="hidden" name="{{encode.Encode(page.ReturnUrlParameter)}}" value="{{encode.Encode(page.ReturnUrl)}}">
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
