using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

// Only the service constructs this wrapper, after evaluating write authorization.
// Raw IContentStore writes remain available to trusted import and maintenance code.
internal sealed class DocumentStagingContent(IDocumentRepository documents, IContentStore content,
    DocumentActorAdmission admission, StorageLimits limits) : IContentStore, IContentBatchWriter, IContentBatchReader
{
    public bool Durable => content.Durable;
    public bool Shared => content.Shared;

    public async ValueTask<IReadOnlyList<ContentHandle>> WriteManyAsync(IReadOnlyList<ReadOnlyMemory<byte>> contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (contents.Count > IContentBatchWriter.MaxBatchObjects)
            throw new ArgumentOutOfRangeException(nameof(contents));
        var handles = new ContentHandle[contents.Count];
        long total = 0;
        for (var i = 0; i < contents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageLimits.Check("object bytes", contents[i].Length, limits.MaxObjectBytes);
            total = checked(total + contents[i].Length);
            StorageLimits.Check("batch content bytes", total, IContentBatchWriter.MaxBatchBytes);
            var hash = Convert.ToHexStringLower(SHA256.HashData(contents[i].Span));
            handles[i] = new(hash, contents[i].Length, hash);
        }
        await ReserveAsync(handles, cancellationToken);
        var written = await content.WriteManyAsync(contents, cancellationToken);
        if (!handles.SequenceEqual(written)) throw new StorageCorruptionException("Staged content differs from its document reservation.");
        return written;
    }

    public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = Path.Combine(Path.GetTempPath(), "cellbridge-document-stage-" + Guid.NewGuid().ToString("N"));
        await using var spool = ConfidentialFileSystem.CreateFile(path, FileAccess.ReadWrite,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            length = checked(length + read);
            StorageLimits.Check("object bytes", length, limits.MaxObjectBytes);
            hash.AppendData(buffer.AsSpan(0, read));
            await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        var handle = new ContentHandle(digest, length, digest);
        await ReserveAsync([handle], cancellationToken);
        spool.Position = 0;
        var written = await content.WriteAsync(spool, cancellationToken);
        if (written != handle) throw new StorageCorruptionException("Staged content differs from its document reservation.");
        return written;
    }

    private async ValueTask ReserveAsync(IReadOnlyCollection<ContentHandle> handles, CancellationToken ct)
    {
        if (handles.Count != 0 && !await documents.ReserveContentAsync(admission, handles, ct))
            throw new StagingAdmissionChangedException();
    }

    public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
        content.OpenReadAsync(handle, cancellationToken);

    public ValueTask<IReadOnlyDictionary<string, byte[]>> ReadManyAsync(IReadOnlyCollection<ContentHandle> handles,
        CancellationToken cancellationToken = default) => content is IContentBatchReader reader
            ? reader.ReadManyAsync(handles, cancellationToken)
            : content.ReadManyVerifiedAsync(handles, limits.MaxObjectBytes, cancellationToken);
}

internal sealed class StagingAdmissionChangedException : IOException;
