namespace CellBridge.DocumentLibrary;

internal static class DocumentLibraryAuthentication
{
    // MS-OFBA section 2.2.2. Keep these paths aligned with the local sign-in endpoints.
    // The repository's Identity-backed authentication host is not a NuGet dependency of this example.
    public static void OfficeChallenge(HttpContext context, string origin)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-FORMS_BASED_AUTH_REQUIRED"] = origin + "/auth/login?returnUrl=%2Fauth%2Fcomplete";
        context.Response.Headers["X-FORMS_BASED_AUTH_RETURN_URL"] = origin + "/auth/complete";
        context.Response.Headers["X-FORMS_BASED_AUTH_DIALOG_SIZE"] = "800x600";
    }

    public static string LocalReturnUrl(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith('/') && !value.StartsWith("//") && !value.Contains('\\') &&
        !value.Any(char.IsControl) ? value : "/auth/complete";
}
