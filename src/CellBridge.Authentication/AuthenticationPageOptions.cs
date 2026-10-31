namespace CellBridge.Authentication;

public sealed class AuthenticationPageOptions
{
    public string ApplicationName { get; set; } = "CellBridge";

    internal bool EntraLoginEnabled { get; set; }
}
