using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.Tests;
using CellBridge.TestFixtures;

namespace CellBridge.Tests;

public static class DocumentStagingScenario
{
    public static StorageLimits Limits { get; } = new() { MaxDocumentStoredBytes = 160_000 };
    private static readonly CellBridgeActor Actor = new(new("staging:writer", "writer", "Writer"), CanCreate: true);

    // This also runs unchanged with the service reservation wrapper disabled to demonstrate the regression.
    public static async Task RejectedServiceSavesStayBoundedAsync(StorageProvider provider)
    {
        var content = new CountingContent(provider.Content);
        var rejected = new RejectPublication(provider.Documents);
        var service = new CellBridgeDocumentService(new(rejected, content, provider.Limits));
        var head = (await service.CreateAsync("/rejected-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), Actor))!;
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(head.ResourceId, HostLockOperation.Acquire, "host", Actor)).Success);
        var before = (await provider.Documents.ReadControlAsync(head.ResourceId))!.Head;
        var reachedQuota = false;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var bytes = MinimalDocx.Create(Convert.ToBase64String(RandomNumberGenerator.GetBytes(12_000)));
            using var input = new MemoryStream(bytes);
            try { await service.SaveWholeFileAsync(head.ResourceId, input, "rejected-" + attempt, Actor, "host"); }
            catch (RejectedPublicationException) { }
            catch (StorageQuotaExceededException) { reachedQuota = true; break; }
        }
        Assert.True(reachedQuota, "Repeated rejected saves must exhaust their charged document budget.");
        var after = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!;
        Assert.Equal(before.StateVersion, after.Current.Head.StateVersion);
        Assert.Equal(before.ContentVersion, after.Current.Head.ContentVersion);
        Assert.Equal(before.LatestRevisionNumber, after.Current.Head.LatestRevisionNumber);
        Assert.Empty(after.Receipts);
        Assert.Contains(after.ContentReferences, reference => reference.IsPending);
        Assert.Equal(after.Current.Head.Counters.StoredBytes, NormalizedDocumentRecovery.StoredBytes(after));
        Assert.InRange(after.Current.Head.Counters.StoredBytes, before.Counters.StoredBytes + 1, provider.Limits.MaxDocumentStoredBytes);
        Assert.True(content.WrittenBytes <= provider.Limits.MaxDocumentStoredBytes);

        var writes = content.Writes;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var input = new MemoryStream(MinimalDocx.Create(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24_000))));
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => service.SaveWholeFileAsync(head.ResourceId, input,
                "exhausted-" + attempt, Actor, "host").AsTask());
        }
        Assert.Equal(writes, content.Writes);
    }

    public static async Task ReservationDeduplicatesAndDoesNotAdvanceVersionsAsync(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var first = (await service.CreateAsync("/first-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), Actor))!;
        var second = (await service.CreateAsync("/second-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), Actor))!;
        var handle = Handle([17, 18, 19]);
        var admission = Admission(first);
        Assert.True(await provider.Documents.ReserveContentAsync(admission, [handle, handle]));
        var reserved = (await provider.Documents.ExportDocumentAsync(first.ResourceId))!;
        var expectedCharge = handle.Length + NormalizedContentReference.StagingMetadataBytes(handle);
        Assert.Equal(first.Counters.StoredBytes + expectedCharge, reserved.Current.Head.Counters.StoredBytes);
        Assert.Equal(first.StateVersion, reserved.Current.Head.StateVersion);
        Assert.Equal(first.ContentVersion, reserved.Current.Head.ContentVersion);
        Assert.Equal(first.LatestRevisionNumber, reserved.Current.Head.LatestRevisionNumber);
        var pending = Assert.Single(reserved.ContentReferences.Where(reference => reference.Handle == handle));
        Assert.True(pending.IsPending);
        Assert.Equal(NormalizedContentReference.StagingMetadataBytes(handle), pending.AdmissionBytes);
        Assert.True(await provider.Documents.ReserveContentAsync(admission, [handle]));
        Assert.Equal(reserved.Current.Head, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(first.ResourceId)))!);
        Assert.True(await provider.Documents.ReserveContentAsync(Admission(second), [handle]));
        Assert.Equal(second.Counters.StoredBytes + expectedCharge,
            (await provider.Documents.FindAsync(new DocumentIdentity.Resource(second.ResourceId)))!.Counters.StoredBytes);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => provider.Documents.ReserveContentAsync(admission,
            [handle, handle with { Length = handle.Length + 1 }]).AsTask());
        await Assert.ThrowsAsync<StorageCorruptionException>(() => provider.Documents.ReserveContentAsync(admission,
            [handle with { Sha256 = new string('z', 64) }]).AsTask());
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => provider.Documents.ReserveContentAsync(admission,
            [Handle([99]) with { Length = provider.Limits.MaxDocumentStoredBytes }]).AsTask());
        Assert.False(await provider.Documents.ReserveContentAsync(admission with { StateVersion = first.StateVersion + 1 }, [Handle([88])]));
        Assert.False(await provider.Documents.ReserveContentAsync(admission with { LifecycleGeneration = first.LifecycleGeneration + 1 }, [Handle([88])]));
        Assert.False(await provider.Documents.ReserveContentAsync(admission with { AuthorityGeneration = first.AuthorityGeneration + 1 }, [Handle([88])]));
        Assert.False(await provider.Documents.ReserveContentAsync(admission with { Access = DocumentAccess.Read }, [Handle([88])]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.Documents.ReserveContentAsync(admission, [Handle([77])], cancellation.Token).AsTask());
        Assert.Equal(reserved.Current.Head, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(first.ResourceId)))!);
        NormalizedDocumentRecovery.Validate((await provider.Documents.ExportDocumentAsync(first.ResourceId))!, provider.Limits);
    }

    public static async Task SuccessfulServiceSaveCompletesReservationsAsync(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var head = (await service.CreateAsync("/accepted-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), Actor))!;
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(head.ResourceId, HostLockOperation.Acquire, "host", Actor)).Success);
        var bytes = MinimalDocx.Create("accepted staging");
        using var input = new MemoryStream(bytes);
        await service.SaveWholeFileAsync(head.ResourceId, input, "accepted", Actor, "host");
        var snapshot = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!;
        Assert.DoesNotContain(snapshot.ContentReferences, reference => reference.IsPending);
        Assert.Contains(snapshot.ContentReferences, reference => reference.AdmissionBytes > 0);
        Assert.Equal(snapshot.Current.Head.Counters.StoredBytes, NormalizedDocumentRecovery.StoredBytes(snapshot));
        var current = snapshot.Current.Head;
        using var retry = new MemoryStream(bytes);
        Assert.True((await service.SaveWholeFileAsync(head.ResourceId, retry, "accepted", Actor, "host")).IsReplay);
        Assert.Equal(current, (await provider.Documents.ReadControlAsync(head.ResourceId))!.Head);
    }

    public static DocumentActorAdmission Admission(DocumentHead head) => new(Actor.Identity, head.ResourceId,
        head.LifecycleGeneration, head.Security.AuthorizationPolicy, DocumentAccess.Write)
        { AuthorityGeneration = head.AuthorityGeneration, StateVersion = head.StateVersion };

    public static async Task MissingPendingContentSurvivesPortableRecoveryAsync(StorageProvider destination)
    {
        var sourceContent = new InMemoryContentStore();
        var source = new StorageProvider(new InMemoryDocumentRepository(sourceContent), sourceContent);
        var head = (await new CellBridgeDocumentService(source).CreateAsync("/pending-recovery.docx", MinimalDocx.Create(), Actor))!;
        var missing = Handle([93, 94, 95]);
        Assert.True(await source.Documents.ReserveContentAsync(Admission(head), [missing]));
        var before = (await source.Documents.ExportDocumentAsync(head.ResourceId))!;
        var path = Path.Combine(Path.GetTempPath(), "cellbridge-pending-recovery-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var archive = new PortableArchive();
            var context = new RecoveryContext(RecoveryAuthorizationContext.StoredGrants("staging-test"), []);
            await archive.ExportAsync(source, path, context);
            await archive.ImportAsync(destination, path, context);
            var after = (await destination.Documents.ExportDocumentAsync(head.ResourceId))!;
            var pending = Assert.Single(after.ContentReferences.Where(reference => reference.IsPending));
            Assert.Equal(missing, pending.Handle);
            Assert.Equal(before.Current.Head, after.Current.Head);
            Assert.Equal(before.ContentReferences.Length, after.ContentReferences.Length);
            Assert.Equal(after.Current.Head.Counters.StoredBytes, NormalizedDocumentRecovery.StoredBytes(after));
            await Assert.ThrowsAsync<StorageCorruptionException>(() => destination.Content.OpenReadAsync(missing).AsTask());
        }
        finally { File.Delete(path); }
    }

    public static async Task ConcurrentReservationsStayWithinDocumentCapacityAsync(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var first = (await service.CreateAsync("/concurrent-first.docx", MinimalDocx.Create(), Actor))!;
        var second = (await service.CreateAsync("/concurrent-second.docx", MinimalDocx.Create(), Actor))!;
        var handles = new[] { Handle(RandomNumberGenerator.GetBytes(90_000)), Handle(RandomNumberGenerator.GetBytes(90_000)) };
        var attempts = handles.Select(handle => Task.Run(async () =>
        {
            try { return await provider.Documents.ReserveContentAsync(Admission(first), [handle]); }
            catch (StorageQuotaExceededException) { return false; }
        })).ToArray();
        Assert.Single(await Task.WhenAll(attempts), accepted => accepted);
        var after = (await provider.Documents.ExportDocumentAsync(first.ResourceId))!;
        Assert.InRange(after.Current.Head.Counters.StoredBytes, 90_000, provider.Limits.MaxDocumentStoredBytes);
        Assert.Equal(first.StateVersion, after.Current.Head.StateVersion);
        Assert.Single(after.ContentReferences.Where(reference => reference.IsPending));
        Assert.True(await provider.Documents.ReserveContentAsync(Admission(second), [handles[0]]));
    }
    public static ContentHandle Handle(byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(hash, bytes.Length, hash);
    }
    private sealed class RejectedPublicationException : Exception;
    private sealed class RejectPublication(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public override ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken ct = default) =>
            throw new RejectedPublicationException();
    }
    private sealed class CountingContent(IContentStore inner) : IContentStore
    {
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public int Writes;
        public long WrittenBytes;
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            var handle = await inner.WriteAsync(source, cancellationToken);
            Writes++;
            if (_keys.Add(handle.Key)) WrittenBytes += handle.Length;
            return handle;
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => inner.OpenReadAsync(handle, cancellationToken);
    }
}
