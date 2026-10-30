namespace CellBridge.AspNetCore;

/// <summary>The host's single document catalog identity and versioning settings page.</summary>
public sealed record OfficeDocumentLibrary
{
    public OfficeDocumentLibrary(Guid id, string settingsUrl)
    {
        if (id == Guid.Empty) throw new ArgumentException("A stable document library GUID is required.", nameof(id));
        if (!Uri.TryCreate(settingsUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new ArgumentException("An absolute HTTP(S) settings URL without credentials is required.", nameof(settingsUrl));
        Id = id;
        SettingsUrl = settingsUrl;
    }

    public Guid Id { get; }
    public string SettingsUrl { get; }
}
