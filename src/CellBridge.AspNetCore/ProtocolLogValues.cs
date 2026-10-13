namespace CellBridge.AspNetCore;

internal static class ProtocolLogValues
{
    public static string Path(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "<empty>";
        // Drop user info, authority, query and fragment before normal logging.
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            value = uri.AbsolutePath;
        else
        {
            if (!value.StartsWith('/') || value.StartsWith("//")) return "<invalid-url>";
            var end = value.IndexOfAny(['?', '#']);
            if (end >= 0) value = value[..end];
        }
        return Text(value);
    }

    public static string Text(string value) => new(value.Take(512).Select(c => char.IsControl(c) ? '_' : c).ToArray());
}
