namespace CellBridge.Storage.Abstractions;

/// <summary>Optional publication of provisional bytes and document records in one database transaction.</summary>
public interface IAtomicDocumentPublisher
{
    bool SupportsAtomicContent(IContentStore content);
    ValueTask<PublicationResult> PublishWithContentAsync(PreparedDocumentPublication publication, IContentStore content,
        IReadOnlyList<ReadOnlyMemory<byte>> contents, CancellationToken cancellationToken = default);
}
