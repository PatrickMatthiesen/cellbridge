namespace CellBridge.DocumentLibrary;

public sealed class DocumentLibraryOptions
{
    public string? DestinationRoot { get; set; }
    public string StorageProvider { get; set; } = "PostgreSql";
    public TimeSpan PublicationInterval { get; set; } = TimeSpan.FromSeconds(1);
    public long MaxUploadBytes { get; set; } = 128L * 1024 * 1024;
    public LibraryAuthorizationCacheOptions AuthorizationCache { get; set; } = new();
}

public sealed class LibraryAuthorizationCacheOptions
{
    public bool Enabled { get; set; }
    public int Capacity { get; set; } = 1024;
    public TimeSpan DecisionTtl { get; set; } = TimeSpan.FromSeconds(10);
}
