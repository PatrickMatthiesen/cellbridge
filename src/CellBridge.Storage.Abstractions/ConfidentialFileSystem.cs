namespace CellBridge.Storage.Abstractions;

/// <summary>
/// Creates confidential files and directories with owner-only Unix permissions.
/// Existing directory permissions are preserved. Windows creation inherits ACLs
/// from the parent directory, which the operator must restrict to trusted identities.
/// </summary>
public static class ConfidentialFileSystem
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Creates each missing directory with owner-only Unix permissions without changing existing ACLs.</summary>
    public static void CreateDirectory(string path)
    {
        var missing = new Stack<string>();
        for (var directory = Path.GetFullPath(path); !Directory.Exists(directory); directory = Path.GetDirectoryName(directory)!)
            missing.Push(directory);
        while (missing.TryPop(out var directory))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, DirectoryMode);
        }
    }

    /// <summary>Creates a new file without a later chmod window or overwriting an existing file.</summary>
    public static FileStream CreateFile(string path, FileAccess access = FileAccess.ReadWrite,
        FileOptions options = FileOptions.Asynchronous, int bufferSize = 65536, FileShare share = FileShare.None)
    {
        var settings = new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = access,
            Share = share,
            BufferSize = bufferSize,
            Options = options,
        };
        if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = FilePermissions;
        return new FileStream(path, settings);
    }
}
