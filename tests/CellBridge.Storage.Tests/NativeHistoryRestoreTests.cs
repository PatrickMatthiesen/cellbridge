using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Tests;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class NativeHistoryRestoreTests
{
    private const string NativeRestoreMarker = "29358EC1-E813-4793-8E70-ED0344E7B73C";

    [Fact]
    public async Task NativeWordRestoreUsesTheSoleSchemaLeaseAndPublishesSelectedHistoricalFile()
    {
        var fixture = await WithSavedRevisionAsync();
        var schema = await AcquireSchemaAsync(fixture, TestActor.Value, clientId: Guid.NewGuid());

        var result = await fixture.Service.RestoreRevisionAsync(fixture.Head.ResourceId, 1, 2,
            "native-word-restore", TestActor.Value, NativeAttributes());

        Assert.False(result.IsReplay);
        Assert.Equal(3UL, result.Revision.RevisionNumber);
        Assert.Equal(fixture.OriginalBytes,
            await StorageTestDocuments.PackageAsync(fixture.Provider, result.Revision.Package));
        var current = (await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.Head.ResourceId)))!;
        Assert.Equal(3, current.Counters.Revisions);
        Assert.Equal(3U, current.ContentVersion);
        Assert.Single((await fixture.Provider.Documents.ReadControlAsync(fixture.Head.ResourceId))!.Coordination.SchemaOwners);
        Assert.Contains(schema.ClientId, (await fixture.Provider.Documents.ReadControlAsync(fixture.Head.ResourceId))!
            .Coordination.SchemaOwners.Select(owner => owner.Client));
    }

    [Fact]
    public async Task NativeRestoreRejectsAnotherSubjectsSoleSchemaLeaseWithoutPublishing()
    {
        var fixture = await WithSavedRevisionAsync();
        var other = new CellBridgeActor(new("other:native-history", "other-native-history", "Other editor"));
        await GrantWriteAsync(fixture, other);
        await AcquireSchemaAsync(fixture, other, Guid.NewGuid());

        await AssertRejectedWithoutHistoryAdvanceAsync(fixture,
            () => fixture.Service.RestoreRevisionAsync(fixture.Head.ResourceId, 1, 2,
                "native-other-owner", TestActor.Value, NativeAttributes()).AsTask());
    }

    [Fact]
    public async Task NativeRestoreRejectsMultipleSchemaClientsOwnedByTheSameSubjectWithoutPublishing()
    {
        var fixture = await WithSavedRevisionAsync();
        var sharedSchemaId = Guid.NewGuid();
        await AcquireSchemaAsync(fixture, TestActor.Value, Guid.NewGuid(), sharedSchemaId);
        await AcquireSchemaAsync(fixture, TestActor.Value, Guid.NewGuid(), sharedSchemaId);

        await AssertRejectedWithoutHistoryAdvanceAsync(fixture,
            () => fixture.Service.RestoreRevisionAsync(fixture.Head.ResourceId, 1, 2,
                "native-multiple-clients", TestActor.Value, NativeAttributes()).AsTask());
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("second-client")]
    public async Task NativeRestoreCannotPublishAfterAuthorityChanges(string mutation)
    {
        var fixture = await WithSavedRevisionAsync();
        var schemaId = Guid.NewGuid();
        await AcquireSchemaAsync(fixture, TestActor.Value, Guid.NewGuid(), schemaId);
        var gate = new PausedPublication(fixture.Provider.Documents);
        var service = new CellBridgeDocumentService(new(gate, fixture.Provider.Content));
        var restoring = service.RestoreRevisionAsync(fixture.Head.ResourceId, 1, 2,
            "native-authority-race", TestActor.Value, NativeAttributes()).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            if (mutation == "second-client")
                await AcquireSchemaAsync(fixture, TestActor.Value, Guid.NewGuid(), schemaId);
            else
            {
                var current = (await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.Head.ResourceId)))!;
                Assert.True((await fixture.Provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(
                    current.ResourceId, StorageTestDocuments.Admission(current),
                    current.Security with { Owner = "revoked" }, current.AuthorityGeneration))).Changed);
            }
        }
        finally { gate.Resume.TrySetResult(); }

        if (mutation == "permission")
            Assert.Equal("FileUnauthorizedAccess", (await Assert.ThrowsAsync<DocumentOperationLockException>(() => restoring)).ErrorCode);
        else
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => restoring);
        await AssertHistoryUnchangedAsync(fixture);
    }

    [Fact]
    public async Task NativeRestoreRejectsAnActiveHostLockWithoutPublishing()
    {
        var fixture = await WithSavedRevisionAsync();
        var current = (await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.Head.ResourceId)))!;
        var lease = new HostLease("host-history-token", TestActor.Value.Identity.Subject, DateTime.UtcNow.AddMinutes(5));
        Assert.True((await fixture.Provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetHostLease(
            current.ResourceId, StorageTestDocuments.Admission(current), lease, null))).Changed);

        await AssertRejectedWithoutHistoryAdvanceAsync(fixture,
            () => fixture.Service.RestoreRevisionAsync(fixture.Head.ResourceId, 1, 2,
                "native-host-lock", TestActor.Value, NativeAttributes()).AsTask());
    }

    [Fact]
    public async Task NativeRestoreRejectsAnActiveExclusiveLockWithoutPublishing()
    {
        var fixture = await WithSavedRevisionAsync();
        const string exclusiveId = "90CC0C79-7986-4F72-9AA5-7C9E4D8323E5";
        var acquired = await fixture.Provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(
            fixture.Head.ResourceId, StorageTestDocuments.Admission(fixture.Head), new()
            {
                Type = SubRequestType.ExclusiveLock,
                SubRequestDataAttributes =
                {
                    ["ExclusiveLockRequestType"] = "GetLock",
                    ["ExclusiveLockID"] = exclusiveId,
                    ["ClientID"] = Guid.NewGuid().ToString(),
                    ["Timeout"] = "3600",
                },
            }));
        Assert.True(acquired.Changed);

        await AssertRejectedWithoutHistoryAdvanceAsync(fixture,
            () => fixture.Service.RestoreRevisionAsync(fixture.Head.ResourceId, 1, 2,
                "native-exclusive-lock", TestActor.Value, NativeAttributes()).AsTask());
    }

    [Fact]
    public async Task ReaderCannotRestoreThroughNativeMarkerEvenWhenAWriterOwnsTheSchemaLease()
    {
        var fixture = await WithSavedRevisionAsync();
        await AcquireSchemaAsync(fixture, TestActor.Value, Guid.NewGuid());
        var reader = TestActor.Value with { AccessLimit = new(fixture.Head.ResourceId, DocumentAccess.Read) };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RestoreRevisionAsync(
            fixture.Head.ResourceId, 1, 2, "native-reader", reader, NativeAttributes()).AsTask());
        await AssertHistoryUnchangedAsync(fixture);
    }

    [Fact]
    public async Task ExplicitSchemaLeaseAttributesContinueToRestoreNormally()
    {
        var fixture = await WithSavedRevisionAsync();
        var schema = await AcquireSchemaAsync(fixture, TestActor.Value, Guid.NewGuid());
        var attributes = new Dictionary<string, string>
        {
            ["SchemaLockID"] = schema.SchemaLockId,
            ["ClientID"] = schema.ClientId,
        };

        var result = await fixture.Service.RestoreRevisionAsync(fixture.Head.ResourceId, 1, 2,
            "explicit-schema-restore", TestActor.Value, attributes);

        Assert.Equal(fixture.OriginalBytes,
            await StorageTestDocuments.PackageAsync(fixture.Provider, result.Revision.Package));
        await AssertHistoryAdvancedAsync(fixture, result.Revision.RevisionNumber);
    }

    [Fact]
    public async Task ExplicitExclusiveLeaseAttributesContinueToRestoreNormally()
    {
        var fixture = await WithSavedRevisionAsync();
        const string lockId = "11A2E88C-4602-4FE5-9BD0-B2E999255A14";
        var clientId = Guid.NewGuid().ToString();
        var acquired = await fixture.Provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(
            fixture.Head.ResourceId, StorageTestDocuments.Admission(fixture.Head), new()
            {
                Type = SubRequestType.ExclusiveLock,
                SubRequestDataAttributes =
                {
                    ["ExclusiveLockRequestType"] = "GetLock",
                    ["ExclusiveLockID"] = lockId,
                    ["ClientID"] = clientId,
                    ["Timeout"] = "3600",
                },
            }));
        Assert.True(acquired.Changed);

        var result = await fixture.Service.RestoreRevisionAsync(fixture.Head.ResourceId, 1, 2,
            "explicit-exclusive-restore", TestActor.Value, new Dictionary<string, string>
            {
                ["ExclusiveLockID"] = lockId,
                ["ClientID"] = clientId,
                ["Timeout"] = "3600",
            });

        Assert.Equal(fixture.OriginalBytes,
            await StorageTestDocuments.PackageAsync(fixture.Provider, result.Revision.Package));
        await AssertHistoryAdvancedAsync(fixture, result.Revision.RevisionNumber);
    }

    private static async Task<Fixture> WithSavedRevisionAsync()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var original = MinimalDocx.Create("original history version");
        var head = (await service.CreateAsync("/native-history-restore.docx", original, TestActor.Value))!;
        var save = await StorageTestDocuments.SaveRequestAsync(service, head,
            MinimalDocx.Create("current history version"));
        var saved = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
            save, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(saved.Response.SubResponses[0].Status);
        return new(provider, service, saved.State, original);
    }

    private static async Task<(string SchemaLockId, string ClientId)> AcquireSchemaAsync(Fixture fixture,
        CellBridgeActor actor, Guid clientId, Guid? schemaLockId = null)
    {
        var id = (schemaLockId ?? Guid.NewGuid()).ToString();
        var client = clientId.ToString();
        var current = (await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.Head.ResourceId)))!;
        var result = await fixture.Provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(
            fixture.Head.ResourceId, StorageTestDocuments.Admission(current, actor), new()
            {
                Type = SubRequestType.SchemaLock,
                SubRequestDataAttributes =
                {
                    ["SchemaLockRequestType"] = "GetLock",
                    ["SchemaLockID"] = id,
                    ["ClientID"] = client,
                    ["Timeout"] = "3600",
                },
            }));
        Assert.True(result.Changed);
        return (id, client);
    }

    private static async Task GrantWriteAsync(Fixture fixture, CellBridgeActor actor)
    {
        var current = (await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.Head.ResourceId)))!;
        var permissions = current.Security with
        {
            Grants = current.Security.Grants.SetItem(actor.Identity.Subject, DocumentAccess.Read | DocumentAccess.Write),
        };
        Assert.True((await fixture.Provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(
            current.ResourceId, StorageTestDocuments.Admission(current), permissions, current.AuthorityGeneration))).Changed);
    }

    private static Dictionary<string, string> NativeAttributes() => new(StringComparer.Ordinal)
    {
        ["VersioningRequestType"] = "RestoreVersion",
        ["Version"] = "1.0",
        ["LockID"] = NativeRestoreMarker,
    };

    private static async Task AssertRejectedWithoutHistoryAdvanceAsync(Fixture fixture, Func<Task> restore)
    {
        var before = (await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.Head.ResourceId)))!;
        await Assert.ThrowsAsync<DocumentOperationLockException>(restore);
        var after = (await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.Head.ResourceId)))!;
        Assert.Equal(before.ContentVersion, after.ContentVersion);
        Assert.Equal(before.Counters.Revisions, after.Counters.Revisions);
        Assert.Equal(before.Counters.RestoreReceipts, after.Counters.RestoreReceipts);
        Assert.Equal(before.Package, after.Package);
    }

    private static async Task AssertHistoryUnchangedAsync(Fixture fixture)
    {
        var after = (await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.Head.ResourceId)))!;
        Assert.Equal(2U, after.ContentVersion);
        Assert.Equal(2, after.Counters.Revisions);
        Assert.Equal(0, after.Counters.RestoreReceipts);
    }

    private static async Task AssertHistoryAdvancedAsync(Fixture fixture, ulong revisionNumber)
    {
        var after = (await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.Head.ResourceId)))!;
        Assert.Equal(3UL, revisionNumber);
        Assert.Equal(3U, after.ContentVersion);
        Assert.Equal(3, after.Counters.Revisions);
        Assert.Equal(1, after.Counters.RestoreReceipts);
    }

    private sealed record Fixture(StorageProvider Provider, CellBridgeDocumentService Service,
        DocumentHead Head, byte[] OriginalBytes);

    private sealed class PausedPublication(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await Inner.PublishAsync(publication, cancellationToken);
        }
    }
}
