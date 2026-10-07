using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.Storage.Abstractions;

namespace CellBridge.DocumentLibrary;

/// <summary>
/// A single-host destination whose manifest is the only mutable pointer. Revision files are immutable.
/// </summary>
public sealed class DocumentLibraryDestination : IExternalRevisionDestination, IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly FileStream _hostLock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDestinationFaults _faults;

    public DocumentLibraryDestination(string root, IDestinationFaults? faults = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _hostLock = new FileStream(Path.Combine(_root, ".cellbridge-document-library.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        _faults = faults ?? new NoDestinationFaults();
    }

    public string Root => _root;

    public async Task<IReadOnlyList<LibraryManifest>> ListAsync(CancellationToken cancellationToken = default)
    {
        var documents = DocumentsRoot();
        if (!Directory.Exists(documents)) return [];
        var result = new List<LibraryManifest>();
        foreach (var path in Directory.EnumerateFiles(documents, "manifest.json", SearchOption.AllDirectories))
            result.Add(await ReadManifestFileAsync(path, cancellationToken));
        return result.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<LibraryManifest?> FindByFileNameAsync(string fileName,
        CancellationToken cancellationToken = default) =>
        (await ListAsync(cancellationToken)).FirstOrDefault(x =>
            string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    public async Task<LibraryManifest> CreateAsync(Guid resourceId, Guid bindingId, string fileName,
        string cellBridgePath, Stream content, ImmutableDictionary<string, DocumentAccess> permissions,
        CancellationToken cancellationToken = default)
    {
        if (resourceId == Guid.Empty) throw new ArgumentException("A resource ID is required.", nameof(resourceId));
        if (bindingId == Guid.Empty) throw new ArgumentException("A binding ID is required.", nameof(bindingId));
        ValidateFileName(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(cellBridgePath);
        if (!cellBridgePath.StartsWith("/shared/", StringComparison.Ordinal))
            throw new ArgumentException("The CellBridge path must be under /shared/.", nameof(cellBridgePath));
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(permissions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await FindByFileNameAsync(fileName, cancellationToken) is not null)
                throw new DocumentNameConflictException("A document with this file name already exists.");
            var manifestPath = ManifestPath(resourceId);
            if (File.Exists(manifestPath)) throw new IOException("The destination document already exists.");
            Directory.CreateDirectory(RevisionsRoot(resourceId));
            var revision = NewRevision();
            var relativeFile = RevisionRelativePath(revision, fileName);
            var stored = await WriteRevisionAsync(resourceId, relativeFile, content, null, cancellationToken);
            var snapshot = new LibraryPermissionSnapshot(1, permissions);
            var manifest = new LibraryManifest(
                LibraryManifest.CurrentFormat,
                resourceId,
                bindingId,
                resourceId.ToString("D"),
                fileName,
                cellBridgePath,
                false,
                revision,
                relativeFile,
                stored.Length,
                stored.Sha256,
                DateTimeOffset.UtcNow,
                1,
                ImmutableDictionary<long, LibraryPermissionSnapshot>.Empty.Add(1, snapshot),
                ImmutableDictionary<Guid, DeliveryReceipt>.Empty);
            await WriteManifestAsync(manifest, overwrite: false, cancellationToken);
            return manifest;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<LibraryManifest> GetAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
        ReadManifestFileAsync(ManifestPath(resourceId), cancellationToken);

    public async Task<LibraryManifest> MarkCellBridgeBoundAsync(Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await GetAsync(resourceId, cancellationToken);
            if (current.CellBridgeBound) return current;
            var next = current with { CellBridgeBound = true };
            await WriteManifestAsync(next, overwrite: true, cancellationToken);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LibraryManifest> PreparePermissionsAsync(Guid resourceId,
        ImmutableDictionary<string, DocumentAccess> permissions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _faults.BeforePermissionPreparationAsync(resourceId, cancellationToken);
            var current = await GetAsync(resourceId, cancellationToken);
            var revision = checked(current.PermissionSnapshots.Keys.DefaultIfEmpty(0).Max() + 1);
            var snapshot = new LibraryPermissionSnapshot(revision, permissions);
            var next = current with
            {
                DesiredPermissionRevision = revision,
                PermissionSnapshots = current.PermissionSnapshots.Add(revision, snapshot),
            };
            await WriteManifestAsync(next, overwrite: true, cancellationToken);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Stream> OpenCurrentReadAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        var manifest = await GetAsync(resourceId, cancellationToken);
        return new FileStream(CurrentPath(manifest), FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async Task<byte[]> ReadCurrentBytesAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenCurrentReadAsync(resourceId, cancellationToken);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, cancellationToken);
        return copy.ToArray();
    }

    public async ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request,
        Stream verifiedContent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(verifiedContent);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Guid.TryParseExact(request.Destination, "D", out var resourceId) ||
                resourceId != request.Revision.ResourceId)
                return new(ExternalDeliveryStatus.Stale);

            var current = await GetAsync(resourceId, cancellationToken);
            var fingerprint = DeliveryFingerprint.From(request);

            // A delayed retry must resolve its original receipt even when a later write advanced the file.
            if (current.Receipts.TryGetValue(request.Revision.OperationId, out var receipt))
            {
                if (receipt.Fingerprint != fingerprint)
                    throw new InvalidOperationException("A delivery operation ID was reused with a different fingerprint.");
                return new(ExternalDeliveryStatus.Applied, receipt.ResultingRevision);
            }

            if (current.BindingId != request.BindingId) return new(ExternalDeliveryStatus.Stale);
            if (!string.Equals(current.CurrentRevision, request.ExpectedRevision, StringComparison.Ordinal))
                return new(ExternalDeliveryStatus.Conflict);

            var nextRevision = NewRevision();
            var relativeFile = RevisionRelativePath(nextRevision, current.FileName);
            var stored = await WriteRevisionAsync(resourceId, relativeFile, verifiedContent,
                request.Revision.Content, cancellationToken);
            var nextReceipt = new DeliveryReceipt(fingerprint, nextRevision, DateTimeOffset.UtcNow);
            var next = current with
            {
                CurrentRevision = nextRevision,
                CurrentFile = relativeFile,
                Length = stored.Length,
                Sha256 = stored.Sha256,
                ModifiedUtc = DateTimeOffset.UtcNow,
                Receipts = current.Receipts.Add(request.Revision.OperationId, nextReceipt),
            };
            await WriteManifestAsync(next, overwrite: true, cancellationToken);
            await _faults.AfterManifestReplacementAsync(next, cancellationToken);
            return new(ExternalDeliveryStatus.Applied, nextRevision);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(long Length, string Sha256)> WriteRevisionAsync(Guid resourceId, string relativeFile,
        Stream source, ContentHandle? expected, CancellationToken cancellationToken)
    {
        var path = ResolveDocumentPath(resourceId, relativeFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long length = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var destination = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 65536,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            });
            var buffer = new byte[65536];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                length = checked(length + read);
                if (expected is not null && length > expected.Length)
                    throw new InvalidDataException("The delivered revision exceeds its declared length.");
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (expected is not null && (length != expected.Length ||
            !string.Equals(sha256, expected.Sha256, StringComparison.Ordinal)))
        {
            TryDelete(path);
            throw new InvalidDataException("The delivered revision does not match its complete content fingerprint.");
        }
        return (length, sha256);
    }

    private async Task WriteManifestAsync(LibraryManifest manifest, bool overwrite,
        CancellationToken cancellationToken)
    {
        var path = ManifestPath(manifest.ResourceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 16384,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            }))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (overwrite)
            {
                File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task<LibraryManifest> ReadManifestFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var resourceId = Guid.TryParse(Path.GetFileName(Path.GetDirectoryName(path)), out var parsed)
            ? parsed
            : Guid.Empty;
        await _faults.AfterManifestOpenedForReadAsync(resourceId, cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<LibraryManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The destination manifest is empty.");
        if (manifest.FormatVersion != LibraryManifest.CurrentFormat || manifest.ResourceId == Guid.Empty ||
            manifest.BindingId == Guid.Empty || manifest.DesiredPermissionRevision <= 0 ||
            !manifest.PermissionSnapshots.ContainsKey(manifest.DesiredPermissionRevision))
            throw new InvalidDataException("The destination manifest is invalid or unsupported.");
        _ = CurrentPath(manifest);
        return manifest;
    }

    private string CurrentPath(LibraryManifest manifest) =>
        ResolveDocumentPath(manifest.ResourceId, manifest.CurrentFile);

    private string ResolveDocumentPath(Guid resourceId, string relativePath)
    {
        var documentRoot = Path.GetFullPath(DocumentRoot(resourceId)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(documentRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(documentRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The manifest file path leaves its document directory.");
        return path;
    }

    private string DocumentsRoot() => Path.Combine(_root, "documents");
    private string DocumentRoot(Guid resourceId) => Path.Combine(DocumentsRoot(), resourceId.ToString("D"));
    private string RevisionsRoot(Guid resourceId) => Path.Combine(DocumentRoot(resourceId), "revisions");
    private string ManifestPath(Guid resourceId) => Path.Combine(DocumentRoot(resourceId), "manifest.json");
    private static string RevisionRelativePath(string revision, string fileName) =>
        "revisions/" + revision + Path.GetExtension(fileName).ToLowerInvariant();
    private static string NewRevision() => Guid.NewGuid().ToString("D");

    private static void ValidateFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Contains('/') || fileName.Contains('\\') ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A plain file name is required.", nameof(fileName));
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".docx" or ".xlsx" or ".pptx"))
            throw new ArgumentException("Only .docx, .xlsx, and .pptx files are supported.", nameof(fileName));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _hostLock.Dispose();
        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
