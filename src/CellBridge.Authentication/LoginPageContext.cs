using Microsoft.AspNetCore.Antiforgery;

namespace CellBridge.Authentication;

/// <summary>Values for a host's sign-in page. HTML-encode values when rendering them.</summary>
public sealed record LoginPageContext(
    string ApplicationName,
    AntiforgeryTokenSet Antiforgery,
    string ReturnUrl,
    bool SignInFailed);
