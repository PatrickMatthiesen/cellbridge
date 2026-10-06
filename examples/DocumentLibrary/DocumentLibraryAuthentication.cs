namespace CellBridge.DocumentLibrary;

internal static class DocumentLibraryAuthentication
{
    public static string LocalReturnUrl(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith('/') && !value.StartsWith("//") && !value.Contains('\\') &&
        !value.Any(char.IsControl) ? value : "/_cellbridge/auth/complete";
}
