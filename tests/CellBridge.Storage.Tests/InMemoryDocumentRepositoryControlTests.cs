using System.Collections.Immutable;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;

public sealed partial class InMemoryDocumentRepositoryTests
{
    [Fact]
    public async Task ProviderTimeRejectsALeaseThatExpiredAfterPreparation()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content, clock: clock);
        var seed = await SeedAsync(content, "/lease.docx");
        seed = seed with { Coordination = CoordinationState.Empty with
        { Exclusive = new("exclusive", "client", clock.GetUtcNow().UtcDateTime.AddSeconds(10), 0, null, Actor.Subject) } };
        Assert.True((await repository.CreateAsync(seed)).Created);
        var prepared = Publication((await repository.ReadControlAsync(seed.Head.ResourceId))!, "expired") with
        { LockAttributes = ImmutableDictionary<string, string>.Empty.Add("ExclusiveLockID", "exclusive") };
        clock.Advance(TimeSpan.FromSeconds(11));
        var result = await repository.PublishAsync(prepared);
        Assert.Equal("InvalidCoauthSession", result.ErrorCode);
        Assert.Equal(0, result.Current.StateVersion);
        Assert.Equal(0, result.Current.Counters.SaveReceipts);
    }

    [Fact]
    public async Task AcceptedReceiptReplaysAfterItsLeaseExpiresWithoutReapplyingTheMutation()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content, clock: clock);
        var seed = await SeedAsync(content, "/lease-retry.docx");
        seed = seed with { Coordination = CoordinationState.Empty with
            { Exclusive = new("exclusive", "client", clock.GetUtcNow().UtcDateTime.AddSeconds(10), 0, null, Actor.Subject) } };
        Assert.True((await repository.CreateAsync(seed)).Created);
        var publication = Publication((await repository.ReadControlAsync(seed.Head.ResourceId))!, "accepted-before-expiry") with
            { LockAttributes = ImmutableDictionary<string, string>.Empty.Add("ExclusiveLockID", "exclusive") };
        var accepted = await repository.PublishAsync(publication);
        Assert.Equal(PublicationStatus.Accepted, accepted.Status);
        clock.Advance(TimeSpan.FromSeconds(11));
        var retry = publication with { Admission = Admission(accepted.Current) };
        var replay = await repository.PublishAsync(retry);
        Assert.Equal(PublicationStatus.Replay, replay.Status);
        Assert.Equal(accepted.Current, replay.Current);
        Assert.Equal(1, replay.Current.Counters.SaveReceipts);
    }

    [Fact]
    public async Task AcceptedPublicationExpiresEditorsOnceAndReplayLeavesControlUnchanged()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow); var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content, clock: clock);
        var seed = await SeedAsync(content, "/publication-expiry.docx");
        Assert.True((await repository.CreateAsync(seed)).Created);
        var current = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var request = new FssHttpSubRequest { Type = SubRequestType.Coauth,
            SubRequestDataAttributes = { ["CoauthRequestType"] = "JoinCoauthoring", ["SchemaLockID"] = "schema",
                ["ClientID"] = Guid.NewGuid().ToString(), ["Timeout"] = "3600" } };
        Assert.True((await repository.ExecuteControlAsync(new DocumentControlCommand.Office(seed.Head.ResourceId,
            Admission(current.Head), request))).Changed);
        var joined = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        Assert.Single(joined.Editors); Assert.Single(joined.Coordination.SchemaOwners);
        var publication = Publication(joined, "expiry-on-publication");
        clock.Advance(TimeSpan.FromDays(2));
        var accepted = await repository.PublishAsync(publication);
        Assert.Equal(PublicationStatus.Accepted, accepted.Status);
        var expired = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        Assert.Empty(expired.Editors); Assert.Empty(expired.Coordination.SchemaOwners);
        Assert.Equal(joined.Head.StateVersion + 1, expired.Head.StateVersion);
        Assert.Equal(joined.Coordination.Generation + 1, expired.Coordination.Generation);
        Assert.Equal(expired.Coordination.Generation, expired.Head.CoordinationGeneration);
        Assert.Equal(0, expired.Head.ActiveEditors);
        Assert.Null(expired.Head.NextEditorExpiryUtc);
        Assert.Equal(joined.Partitions.Single(p => p.Kind == 2).Knowledge + 1, expired.Partitions.Single(p => p.Kind == 2).Knowledge);
        NormalizedDocumentRecovery.Validate((await repository.ExportDocumentAsync(seed.Head.ResourceId))!, new());
        var replay = await repository.PublishAsync(publication with { Admission = Admission(expired.Head) });
        Assert.Equal(PublicationStatus.Replay, replay.Status);
        Assert.Equal(expired.Head, replay.Current);
        Assert.Equal(expired.Coordination, (await repository.ReadControlAsync(seed.Head.ResourceId))!.Coordination);
    }

    [Fact]
    public async Task MalformedLockDoesNotPublishIncidentalEditorExpiry()
    {
        var control = await ControlWithEditorAsync(expired: true);
        var request = new FssHttpSubRequest { Type = SubRequestType.ExclusiveLock,
            SubRequestDataAttributes = { ["ExclusiveLockRequestType"] = "GetLock", ["ExclusiveLockID"] = "exclusive", ["ClientID"] = "client", ["Timeout"] = "bad" } };
        var result = DocumentControlReducer.Apply(control, new DocumentControlCommand.Office(control.Head.ResourceId,
            Admission(control.Head), request), new());
        Assert.Equal("InvalidArgument", result.ErrorCode);
        Assert.False(result.Changed);
        Assert.Same(control, result.Current);
        Assert.Single(result.Current.Editors);
    }

    [Fact]
    public async Task AnotherActorCannotUseAnExistingEditorClientIdentity()
    {
        var control = await ControlWithEditorAsync(expired: false);
        var other = Admission(control.Head) with { Actor = new("other", "other", "Other") };
        var request = new FssHttpSubRequest { Type = SubRequestType.EditorsTable,
            SubRequestDataAttributes = { ["EditorsTableRequestType"] = "LeaveEditingSession", ["ClientID"] = control.Editors[0].ClientId.ToString() } };
        var result = DocumentControlReducer.Apply(control, new DocumentControlCommand.Office(control.Head.ResourceId, other, request), new());
        Assert.Equal("FileUnauthorizedAccess", result.ErrorCode);
        Assert.False(result.Changed);
        Assert.Single(result.Current.Editors);
    }

    [Fact]
    public async Task RevocationPrunesParticipantsAndLeasesAndAdvancesTheAuthorityEpoch()
    {
        var control = await ControlWithEditorAsync(expired: false);
        var other = new SubjectIdentity("other", "other", "Other");
        control = control with
        {
            Editors = [control.Editors[0] with { Owner = other }],
            Coordination = new("schema", [new("schema", "client", control.UtcNow.AddHours(1), 1, "schema", other.Subject)],
                new("exclusive", "client", control.UtcNow.AddHours(1), 0, null, other.Subject), 0)
            { HostLock = new("host", other.Subject, control.UtcNow.AddHours(1)), CoauthorClients = ["client"] },
        };
        var result = DocumentControlReducer.Apply(control, new DocumentControlCommand.SetPermissions(control.Head.ResourceId,
            Admission(control.Head), control.Head.Security, control.Head.AuthorityGeneration), new());
        Assert.True(result.Changed);
        Assert.Empty(result.Current.Editors);
        Assert.Empty(result.Current.Coordination.SchemaOwners);
        Assert.Empty(result.Current.Coordination.CoauthorClients);
        Assert.Null(result.Current.Coordination.Exclusive);
        Assert.Null(result.Current.Coordination.HostLock);
        Assert.Equal(control.Head.AuthorityGeneration + 1, result.Current.Head.AuthorityGeneration);
        Assert.True(result.Current.Partitions.Single(p => p.Kind == 2).Knowledge > control.Partitions.Single(p => p.Kind == 2).Knowledge);
    }

    [Fact]
    public async Task EditorExpiryAdvancesKnowledgeOnceAndPersistsTheInlinePartition()
    {
        var control = await ControlWithEditorAsync(expired: true);
        var first = DocumentControlReducer.Apply(control, new DocumentControlCommand.ExpireEditors(control.Head.ResourceId, Admission(control.Head)), new());
        Assert.True(first.Changed);
        Assert.Empty(first.Current.Editors);
        var partition = first.Current.Partitions.Single(p => p.Kind == 2);
        Assert.True(partition.Knowledge > control.Partitions.Single(p => p.Kind == 2).Knowledge);
        var second = DocumentControlReducer.Apply(first.Current, new DocumentControlCommand.ExpireEditors(control.Head.ResourceId,
            Admission(first.Current.Head)), new());
        Assert.False(second.Changed);
        Assert.Equal(partition.Knowledge, second.Current.Partitions.Single(p => p.Kind == 2).Knowledge);

        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content);
        var seed = await SeedAsync(content, "/expiry.docx");
        seed = seed with { Editors = control.Editors };
        Assert.True((await repository.CreateAsync(seed)).Created);
        var stored = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        Assert.True((await repository.ExecuteControlAsync(new DocumentControlCommand.ExpireEditors(seed.Head.ResourceId, Admission(stored.Head)))).Changed);
        var expired = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        Assert.Empty(expired.Editors);
        Assert.True(expired.Partitions.Single(p => p.Kind == 2).Knowledge > stored.Partitions.Single(p => p.Kind == 2).Knowledge);
    }

    [Fact]
    public async Task StaleAdmissionsDenyMutationAndCustomUnboundAccessUsesItsExactMetadataFence()
    {
        var (repository, _, seed) = await RepositoryAsync();
        var control = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var admission = Admission(control.Head);
        var changed = await repository.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(seed.Head.ResourceId,
            admission, DocumentSecurity.Empty, control.Head.AuthorityGeneration));
        Assert.True(changed.Changed);
        var denied = await repository.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(seed.Head.ResourceId,
            admission, control.Head.Security, changed.Current.Head.AuthorityGeneration));
        Assert.False(denied.Changed);
        Assert.Equal("FileUnauthorizedAccess", denied.ErrorCode);
        var custom = Admission(changed.Current.Head);
        Assert.Equal(DocumentAccess.Read | DocumentAccess.Write, DocumentControlReducer.Access(changed.Current, custom));
        Assert.Equal(DocumentAccess.None, changed.Current.Head.Security.AccessFor(custom.Actor.Subject));
        var staleMetadata = custom with { StateVersion = custom.StateVersion - 1 };
        Assert.Equal(DocumentAccess.None, DocumentControlReducer.Access(changed.Current, staleMetadata));
    }

    [Fact]
    public async Task PayloadStagingForOneDocumentDoesNotHoldAnotherDocumentsGuard()
    {
        var content = new InMemoryContentStore();
        var paused = new PausedContent(content);
        var repository = new InMemoryDocumentRepository(paused);
        var first = await SeedAsync(content, "/first.docx");
        var second = await SeedAsync(content, "/second.docx");
        Assert.True((await repository.CreateAsync(first)).Created);
        Assert.True((await repository.CreateAsync(second)).Created);
        using var source = new MemoryStream("receipt"u8.ToArray());
        var response = await content.WriteAsync(source);
        paused.Pause = response;
        var publication = Publication((await repository.ReadControlAsync(first.Head.ResourceId))!, "paused");
        publication = publication with { Receipt = publication.Receipt with { Response = response } };
        var pending = repository.PublishAsync(publication).AsTask();
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = (await repository.ReadControlAsync(second.Head.ResourceId))!;
            var changed = await repository.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(second.Head.ResourceId,
                Admission(current.Head), current.Head.Security, current.Head.AuthorityGeneration)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(changed.Changed);
        }
        finally { paused.Continue.TrySetResult(); }
        Assert.Equal(PublicationStatus.Accepted, (await pending).Status);
    }

    private static async Task<DocumentControlRead> ControlWithEditorAsync(bool expired)
    {
        var seed = await SeedAsync(new InMemoryContentStore(), "/control.docx");
        var now = DateTime.UtcNow;
        var editor = new EditorState(Guid.NewGuid(), Actor.Login, now.AddMinutes(-2), now.AddMinutes(-1),
            now.AddMinutes(expired ? -1 : 1), 60, true, 0, ImmutableDictionary<string, ImmutableArray<byte>>.Empty, Actor);
        return new(seed.Head, now, CoordinationState.Empty, [editor], seed.Partitions.Select(p => p.After).ToImmutableArray());
    }

    private sealed class ManualClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
        public void Advance(TimeSpan amount) => instant += amount;
    }

    private sealed class PausedContent(IContentStore inner) : IContentStore
    {
        public ContentHandle? Pause;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Durable => false;
        public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => inner.WriteAsync(source, cancellationToken);
        public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        {
            if (handle == Pause) { Entered.TrySetResult(); await Continue.Task.WaitAsync(cancellationToken); }
            return await inner.OpenReadAsync(handle, cancellationToken);
        }
    }
}
