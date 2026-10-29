using System.Security.Cryptography;
using CellBridge.FssHttpB;

namespace CellBridge.Storage.Abstractions;

/// <summary>Version-bound canonical graph root used to reproduce the exact package bytes.</summary>
public sealed record DocumentPackageRoot(IndexedGraphScope Scope, ExtendedId StorageIndex,
    IndexedCellId Cell, ExtendedId Revision, ExtendedId RootObject);

/// <summary>A package is a derived representation, never a physical content-store key.</summary>
public sealed record DocumentPackage(Guid ResourceId, long LifecycleGeneration,
    ulong RevisionNumber, int ReconstructionVersion, DocumentPackageRoot Root, long Length, string Sha256)
{
    public const int CurrentReconstructionVersion = 1;
}

public interface IDocumentPackageReader
{
    /// <summary>Returns verified bytes for this immutable version. Authorization belongs to the caller.</summary>
    ValueTask<Stream> OpenReadAsync(DocumentPackage package, CancellationToken cancellationToken = default);
}

/// <summary>Produces a verified seekable scratch stream. Closing it removes the temporary package.</summary>
public sealed class GraphDocumentPackageReader(
    Func<DocumentPackage, CancellationToken, ValueTask<IReadOnlyList<IndexedObjectBinding>>> readBindings,
    Func<IndexedGraphScope, ExtendedId, CancellationToken, ValueTask<DataElement>> readElement,
    long maxDocumentBytes) : IDocumentPackageReader
{
    public async ValueTask<Stream> OpenReadAsync(DocumentPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDocumentBytes);
        if (package.ReconstructionVersion != DocumentPackage.CurrentReconstructionVersion ||
            package.ResourceId != package.Root.Scope.ResourceId || package.LifecycleGeneration != package.Root.Scope.LifecycleGeneration ||
            package.Root.Scope.TargetPartition != 0 || package.RevisionNumber == 0 || package.Length < 0 ||
            package.Sha256.Length != 64 || package.Sha256.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new StorageCorruptionException("Invalid or unsupported package reconstruction descriptor.");
        StorageLimits.Check("document bytes", package.Length, maxDocumentBytes);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose, BufferSize = 65536,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), "cellbridge-package-" + Guid.NewGuid().ToString("N")), options);
        try
        {
            var bindings = await readBindings(package, cancellationToken);
            var file = await IndexedDocumentGraph.LoadFileAsync(package.Root, bindings, readElement, cancellationToken);
            await file.MaterializeToAsync(stream, maxDocumentBytes, cancellationToken);
            stream.Position = 0;
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            if (stream.Length != package.Length || hash != package.Sha256)
                throw new StorageCorruptionException("Derived package disagrees with its recorded length or digest.");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}
