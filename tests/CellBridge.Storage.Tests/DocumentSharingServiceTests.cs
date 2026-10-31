using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CellBridge.Storage.Tests;

public sealed class DocumentSharingServiceTests
{
    [Fact]
    public async Task OwnerCanReadGrantAndRevokeWithoutChangingDocumentAuthorMetadata()
    {
        var provider = TestStorage.Memory();
        var service = new CellBridgeDocumentService(provider);
        var owner = TestActor.Value;
        var recipient = new SubjectIdentity("tests:reader", "reader", "Reader");
        var head = (await service.CreateAsync("/shared/share.docx", [1, 2, 3], owner))!;
        var author = head.Security.CreatedBy;
        var modifier = head.Security.ModifiedBy;

        var initial = (await service.GetSharingAsync(head.ResourceId, head.LifecycleGeneration, owner))!;
        Assert.Equal(owner.Identity.Subject, initial.OwnerSubject);
        Assert.Empty(initial.Grants);

        Assert.Equal(DocumentSharingUpdateStatus.Applied, await service.SetRecipientGrantAsync(
            head.ResourceId, initial.LifecycleGeneration, initial.AuthorityGeneration, owner,
            recipient, DocumentAccess.Read));
        var readGranted = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
        Assert.Equal(DocumentAccess.Read, service.Access(new(recipient), readGranted));
        Assert.Equal(author, readGranted.Security.CreatedBy);
        Assert.Equal(modifier, readGranted.Security.ModifiedBy);
        var visible = (await service.GetSharingAsync(head.ResourceId, head.LifecycleGeneration, owner))!;
        Assert.Equal(DocumentAccess.Read, visible.Grants[recipient.Subject]);
        using var admission = new ProtocolRequestAdmission(new CellBridgeOptions());
        var rtc = new OfficeRealTimeAccess(service,
            new EphemeralDataProtectionProvider(NullLoggerFactory.Instance), TimeProvider.System, admission);
        var sharedProperties = await rtc.GetPropertiesAsync(head.ResourceId, owner, "https://office.example", default);
        Assert.Equal("Shared", sharedProperties["SharedStatus"]);

        Assert.Equal(DocumentSharingUpdateStatus.Applied, await service.SetRecipientGrantAsync(
            head.ResourceId, visible.LifecycleGeneration, visible.AuthorityGeneration, owner,
            recipient, DocumentAccess.Read | DocumentAccess.Write));
        var writeGranted = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
        Assert.True(service.Access(new(recipient), writeGranted).HasFlag(DocumentAccess.Write));

        var beforeRevoke = (await service.GetSharingAsync(head.ResourceId, head.LifecycleGeneration, owner))!;
        Assert.Equal(DocumentSharingUpdateStatus.Applied, await service.SetRecipientGrantAsync(
            head.ResourceId, beforeRevoke.LifecycleGeneration, beforeRevoke.AuthorityGeneration, owner,
            recipient, DocumentAccess.None));
        var revoked = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
        Assert.Equal(DocumentAccess.None, service.Access(new(recipient), revoked));
        Assert.False(revoked.Security.Grants.ContainsKey(recipient.Subject));
        Assert.Equal(author, revoked.Security.CreatedBy);
        Assert.Equal(modifier, revoked.Security.ModifiedBy);
        var privateProperties = await rtc.GetPropertiesAsync(head.ResourceId, owner, "https://office.example", default);
        Assert.Equal("Private", privateProperties["SharedStatus"]);
    }

    [Fact]
    public async Task NonOwnerWriterAndOwnerWithReadCeilingCannotManageSharing()
    {
        var service = new CellBridgeDocumentService(TestStorage.Memory());
        var head = (await service.CreateAsync("/shared/share-auth.docx", [1], TestActor.Value))!;
        var writer = new SubjectIdentity("tests:writer2", "writer2", "Writer 2");
        var security = head.Security with
        {
            Grants = head.Security.Grants.SetItem(writer.Subject, DocumentAccess.Read | DocumentAccess.Write)
        };
        await ApplyPermissions(service.Provider, head, security);
        var current = (await service.Provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.GetSharingAsync(
            current.ResourceId, current.LifecycleGeneration, new(writer)));
        var ownerWithCeiling = TestActor.Value with
        {
            AccessLimit = new DocumentAccessLimit(current.ResourceId, DocumentAccess.Read)
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.SetRecipientGrantAsync(
            current.ResourceId, current.LifecycleGeneration, current.AuthorityGeneration, ownerWithCeiling,
            new SubjectIdentity("tests:reader2", "reader2", "Reader 2"), DocumentAccess.Read));
    }

    [Fact]
    public async Task StaleLifecycleAuthorityAndOwnerChangeReturnConflictWithoutGrantMutation()
    {
        var provider = TestStorage.Memory();
        var service = new CellBridgeDocumentService(provider);
        var head = (await service.CreateAsync("/shared/share-fence.docx", [1], TestActor.Value))!;
        var snapshot = (await service.GetSharingAsync(head.ResourceId, head.LifecycleGeneration, TestActor.Value))!;
        Assert.Null(await service.GetSharingAsync(head.ResourceId, head.LifecycleGeneration + 1, TestActor.Value));
        Assert.Equal(DocumentSharingUpdateStatus.Conflict, await service.SetRecipientGrantAsync(
            head.ResourceId, head.LifecycleGeneration + 1, snapshot.AuthorityGeneration, TestActor.Value,
            new("tests:reader3", "reader3", "Reader 3"), DocumentAccess.Read));

        var changedOwner = snapshot.OwnerSubject + ":changed";
        var changedSecurity = head.Security with { Owner = changedOwner };
        await ApplyPermissions(provider, head, changedSecurity);
        Assert.Equal(DocumentSharingUpdateStatus.Conflict, await service.SetRecipientGrantAsync(
            head.ResourceId, snapshot.LifecycleGeneration, snapshot.AuthorityGeneration, TestActor.Value,
            new("tests:reader3", "reader3", "Reader 3"), DocumentAccess.Read));
        var after = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
        Assert.Empty(after.Security.Grants);
        Assert.Equal(changedOwner, after.Security.Owner);
    }

    [Fact]
    public async Task ConcurrentDistinctGrantChangesDoNotOverwriteEachOtherSilently()
    {
        var service = new CellBridgeDocumentService(TestStorage.Memory());
        var head = (await service.CreateAsync("/shared/share-race.docx", [1], TestActor.Value))!;
        var view = (await service.GetSharingAsync(head.ResourceId, head.LifecycleGeneration, TestActor.Value))!;
        var first = service.SetRecipientGrantAsync(head.ResourceId, view.LifecycleGeneration, view.AuthorityGeneration,
            TestActor.Value, new("tests:reader-a", "reader-a", "Reader A"), DocumentAccess.Read).AsTask();
        var second = service.SetRecipientGrantAsync(head.ResourceId, view.LifecycleGeneration, view.AuthorityGeneration,
            TestActor.Value, new("tests:reader-b", "reader-b", "Reader B"), DocumentAccess.Read).AsTask();

        var results = await Task.WhenAll(first, second);
        Assert.Single(results, result => result == DocumentSharingUpdateStatus.Applied);
        Assert.Single(results, result => result == DocumentSharingUpdateStatus.Conflict);
        var after = (await service.Provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
        Assert.Single(after.Security.Grants);
    }

    [Fact]
    public async Task BoundAuthorizationDocumentsRejectStoredGrantSharingExplicitly()
    {
        var owner = TestActor.Value;
        var service = new CellBridgeDocumentService(TestStorage.Memory(), authorizationPolicy: new BoundOwnerPolicy());
        var head = (await service.CreateAsync("/shared/share-bound.docx", [1], owner))!;
        await Assert.ThrowsAsync<NotSupportedException>(async () => await service.GetSharingAsync(
            head.ResourceId, head.LifecycleGeneration, owner));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await service.SetRecipientGrantAsync(
            head.ResourceId, head.LifecycleGeneration, head.AuthorityGeneration, owner,
            new("tests:reader-bound", "reader-bound", "Reader"), DocumentAccess.Read));
    }

    [Fact]
    public async Task OwnerCannotBeChangedThroughRecipientGrantApi()
    {
        var service = new CellBridgeDocumentService(TestStorage.Memory());
        var head = (await service.CreateAsync("/shared/share-owner.docx", [1], TestActor.Value))!;
        await Assert.ThrowsAsync<ArgumentException>(async () => await service.SetRecipientGrantAsync(
            head.ResourceId, head.LifecycleGeneration, head.AuthorityGeneration, TestActor.Value,
            new SubjectIdentity(TestActor.Value.Identity.Subject, "alias", "Alias"), DocumentAccess.Read));
        var unchanged = (await service.Provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
        Assert.Equal(TestActor.Value.Identity.Subject, unchanged.Security.Owner);
        Assert.Empty(unchanged.Security.Grants);
    }

    [Fact]
    public async Task LegacyEvaluatorCannotReportStoredGrantChangesAsEffectiveSharing()
    {
        var provider = TestStorage.Memory();
        var head = (await new CellBridgeDocumentService(provider).CreateAsync("/sharing-legacy.docx", [1], TestActor.Value))!;
        var service = new CellBridgeDocumentService(provider, accessEvaluator: new LegacyAllowAll());
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await service.GetSharingAsync(head.ResourceId, head.LifecycleGeneration, TestActor.Value));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await service.SetRecipientGrantAsync(
            head.ResourceId, head.LifecycleGeneration, head.AuthorityGeneration, TestActor.Value,
            new("reader", "reader", "Reader"), DocumentAccess.Read));
    }

    [Fact]
    public async Task ConcurrentDocumentActivityReturnsConflictInsteadOfDenyingTheOwner()
    {
        var raw = TestStorage.Memory();
        var activity = new ActivityBeforePermissions(raw.Documents);
        var service = new CellBridgeDocumentService(new StorageProvider(activity, raw.Content, raw.Limits));
        var head = (await service.CreateAsync("/sharing-activity.docx", [1], TestActor.Value))!;
        Assert.Equal(DocumentSharingUpdateStatus.Conflict, await service.SetRecipientGrantAsync(
            head.ResourceId, head.LifecycleGeneration, head.AuthorityGeneration, TestActor.Value,
            new("reader", "reader", "Reader"), DocumentAccess.Read));
        var view = (await service.GetSharingAsync(head.ResourceId, head.LifecycleGeneration, TestActor.Value))!;
        Assert.Empty(view.Grants);
        Assert.Equal(head.AuthorityGeneration, view.AuthorityGeneration);
        Assert.Equal(DocumentSharingUpdateStatus.Applied, await service.SetRecipientGrantAsync(
            head.ResourceId, view.LifecycleGeneration, view.AuthorityGeneration, TestActor.Value,
            new("reader", "reader", "Reader"), DocumentAccess.Read));
    }

    private sealed class LegacyAllowAll : ICellBridgeAccessEvaluator
    {
        public DocumentAccess Evaluate(CellBridgeActor actor, DocumentAuthorizationView state) =>
            DocumentAccess.Read | DocumentAccess.Write;
    }

    private sealed class ActivityBeforePermissions(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        private bool advanced;
        public override async ValueTask<ControlResult> ExecuteControlAsync(DocumentControlCommand command, CancellationToken ct = default)
        {
            if (command is DocumentControlCommand.SetPermissions && !advanced)
            {
                advanced = true;
                var current = (await Inner.ReadControlAsync(command.ResourceId, ct))!;
                Assert.True((await Inner.ChangePathAsync(new(command.ResourceId, TestStorage.Admit(current.Head),
                    "/sharing-activity-moved.docx", "/SHARING-ACTIVITY-MOVED.DOCX", ImmutableDictionary<string, string>.Empty), ct)).Changed);
            }
            return await base.ExecuteControlAsync(command, ct);
        }
    }

    private static async Task ApplyPermissions(StorageProvider provider, DocumentHead head, DocumentSecurity security)
    {
        var admission = TestStorage.Admit(head);
        var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(
            head.ResourceId, admission, security, head.AuthorityGeneration));
        Assert.True(result.Changed, result.ErrorCode);
    }

    private sealed class BoundOwnerPolicy : ICellBridgeAuthorizationPolicy
    {
        public string? PolicyDomain => "tests:sharing-bound";
        public DocumentAuthorizationBinding? BindNewDocument(Guid resourceId) => new(PolicyDomain!, 1, 1);
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentAuthorizationView state) =>
            new Snapshot(state.ResourceId, state.Security.AuthorizationPolicy, state.Security.Owner);

        private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding? Binding, string? Owner)
            : ICellBridgeAuthorizationSnapshot
        {
            public DocumentAccess Evaluate(string subject) => subject == Owner
                ? DocumentAccess.Read | DocumentAccess.Write : DocumentAccess.None;
        }
    }
}
