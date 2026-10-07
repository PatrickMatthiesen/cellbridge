using CellBridge.AspNetCore;
using CellBridge.DocumentLibrary;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.Extensions.Logging.Abstractions;

namespace CellBridge.DocumentLibrary.Tests;

internal sealed class DocumentLibraryHarness : IAsyncDisposable
{
    public DocumentLibraryHarness(string root, StorageProvider? provider = null,
        IDestinationFaults? destinationFaults = null)
    {
        Destination = new(root, destinationFaults);
        Provider = provider ?? new(new InMemoryStateStore(), new InMemoryContentStore());
        Permissions = new(Destination);
        Documents = new(Provider, authorizationPolicy: Permissions);
        Publisher = new(Provider, Destination);
        Worker = new(Provider, Publisher, new() { PollingInterval = Options.PublicationInterval }, NullLogger<ExternalRevisionPublicationWorker>.Instance);
        Library = new(Documents, Destination, Permissions, Publisher, Worker, Options);
    }

    public DocumentLibraryOptions Options { get; } = new() { PublicationInterval = TimeSpan.FromMilliseconds(25) };
    public DocumentLibraryDestination Destination { get; }
    public StorageProvider Provider { get; }
    public DocumentLibraryPermissionPolicy Permissions { get; }
    public CellBridgeDocumentService Documents { get; }
    public ExternalRevisionPublisher Publisher { get; }
    public ExternalRevisionPublicationWorker Worker { get; }
    public DocumentLibraryService Library { get; }
    public static CellBridgeActor Owner { get; } = new(new(DocumentLibraryService.OwnerSubject,
        "owner", "Local owner"), true);
    public static CellBridgeActor Editor { get; } = new(new(DocumentLibraryService.EditorSubject,
        "editor", "Local editor"));
    public static CellBridgeActor Reader { get; } = new(new(DocumentLibraryService.ReaderSubject,
        "reader", "Local reader"));

    public async Task<DocumentState> UploadAsync(byte[] bytes, string fileName = "example.docx")
    {
        await using var content = new MemoryStream(bytes, writable: false);
        return await Library.UploadAsync(fileName, content, Owner);
    }

    public static FsshttpbCellRequest SaveRequest(DocumentState state, byte[] content)
    {
        static ExGuid Fresh() => new(1, Guid.NewGuid());
        static ExGuid Restore(ExtendedId id) => new(id.Value, id.Guid);
        var partition = state.Partitions.Single(x => x.Kind == 0);
        var identity = new StorageManifestBuilder.StableIdentity(
            Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(),
            new CellId(Restore(partition.Identity.CellLong), Restore(partition.Identity.CellShort)),
            Guid.NewGuid());
        var graph = FileContentPartitionBuilder.BuildQueryChangesResponse(1, content, identity,
            partition.Knowledge + 100);
        var storageIndex = ((QueryChangesSubResponseData)graph.SubResponses.Single().Data!).StorageIndexExtendedGuid;
        return new FsshttpbCellRequest
        {
            DataElementPackage = graph.DataElementPackage,
            SubRequests =
            {
                new(RequestTypes.PutChanges)
                {
                    RequestId = 1,
                    Data = new PutChangesSubRequestData
                    {
                        StorageIndex = storageIndex,
                        ExpectedStorageIndex = Restore(partition.StorageIndex!),
                    },
                },
            },
        };
    }

    public ValueTask DisposeAsync()
    {
        Worker.Dispose();
        return Destination.DisposeAsync();
    }
}
