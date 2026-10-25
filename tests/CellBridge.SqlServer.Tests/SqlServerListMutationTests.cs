using System.Data.Common;
using System.Text;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

public sealed partial class SqlServerListEngineTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 7)]
    [InlineData(true, 0)]
    [InlineData(true, 7)]
    public async Task SuppressedAccountingSaveRollsBackCreationAndOriginalIdentityRemainsUsable(bool record, int fakeCount)
    {
        var env = await SetupAsync();
        var first = await env.CreateListAsync();
        var before = await env.SnapshotAsync(first.Id);
        var suppress = new SuppressAuditSave(fakeCount);
        var engine = new SqlServerListEngine(env.OptionsWith(suppress));
        var listRequest = NewListRequest();
        var recordRequest = Request(first);
        await Failure(ListFailure.Unavailable, () => record
            ? engine.CreateRecordAsync(Admin, env.Workspace, first.Id, recordRequest)
            : engine.CreateListAsync(Admin, env.Workspace, listRequest));
        Assert.True(suppress.Triggered);
        Assert.Equal(before, await env.SnapshotAsync(first.Id));
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, env.Workspace))
        {
            Assert.False(await scope.Context.Lists.AnyAsync(row => row.ListId == listRequest.Id));
            Assert.False(await scope.Context.ListSchemas.AnyAsync(row => row.ListId == listRequest.Id));
            Assert.False(await scope.Context.ListCounters.AnyAsync(row => row.ListId == listRequest.Id));
            Assert.False(await scope.Context.ListGrants.AnyAsync(row => row.ListId == listRequest.Id));
            Assert.Single(await scope.Context.AuditEvents.ToListAsync());
        }
        if (record)
        {
            var accepted = await env.Engine.CreateRecordAsync(Admin, env.Workspace, first.Id, recordRequest);
            Assert.Equal(1, accepted.Number);
            Assert.False(accepted.Replayed);
            var charged = await env.SnapshotAsync(first.Id);
            Assert.True((await env.Engine.CreateRecordAsync(Admin, env.Workspace, first.Id, recordRequest)).Replayed);
            Assert.Equal(charged, await env.SnapshotAsync(first.Id));
        }
        else
        {
            await env.Engine.CreateListAsync(Admin, env.Workspace, listRequest);
            var created = await env.Engine.CreateRecordAsync(Admin, env.Workspace, listRequest.Id,
                new(Guid.NewGuid(), 1, new JsonObject()));
            Assert.Equal(1, created.Number);
        }
        Assert.Equal(0, await UnscopedCountAsync(env.Runtime));
    }

    [Fact]
    public async Task MembershipGrantFlagsRevocationAndSecurityAuditAreIndependent()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        await env.Engine.SetMembershipAsync(Admin, env.Workspace, "manager", true, false, true);
        await env.Engine.SetMembershipAsync(Admin, env.Workspace, "creator", true, true, false);
        await env.Engine.SetMembershipAsync(Admin, env.Workspace, "writer ", true, false, false);
        await Failure(ListFailure.Forbidden, () => env.Engine.CreateListAsync(new("manager"), env.Workspace, NewListRequest()));
        await env.Engine.CreateListAsync(new("creator"), env.Workspace, NewListRequest());
        await Failure(ListFailure.Forbidden, () => env.Engine.SetMembershipAsync(new("creator"), env.Workspace, "extra", true, true, true));
        await env.Engine.SetListGrantAsync(new("manager"), env.Workspace, list.Id, "writer ", ListAccess.Write);
        var request = Request(list);
        var writer = new ListActor("writer ");
        await env.Engine.CreateRecordAsync(writer, env.Workspace, list.Id, request);
        await Failure(ListFailure.NotFound, () => env.Engine.GetRecordAsync(writer, env.Workspace, list.Id, request.Id));
        await Failure(ListFailure.Forbidden, () => env.Engine.CreateRecordAsync(new("writer"), env.Workspace, list.Id, request));
        var before = await env.SnapshotAsync(list.Id);
        var quota = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxAuditEvents = before.Audits, MaxWorkspaceBytes = before.Bytes });
        // Revocation uses the independent security audit reserve even when the
        // ordinary audit/storage quota cannot admit additions.
        await quota.SetListGrantAsync(new("manager"), env.Workspace, list.Id, writer.Subject, ListAccess.None);
        await Failure(ListFailure.NotFound, () => env.Engine.CreateRecordAsync(writer, env.Workspace, list.Id, request));
        await env.Engine.SetListGrantAsync(Admin, env.Workspace, list.Id, writer.Subject, ListAccess.Read | ListAccess.Write);
        await env.Engine.SetMembershipAsync(Admin, env.Workspace, writer.Subject, false, false, false);
        await Failure(ListFailure.Forbidden, () => env.Engine.CreateRecordAsync(writer, env.Workspace, list.Id, request));
        await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, env.Workspace);
        Assert.False(await scope.Context.ListGrants.AnyAsync(row => row.Subject == writer.Subject));
        Assert.False(await scope.Context.Memberships.AnyAsync(row => row.Subject == writer.Subject));
        Assert.Equal(2, (await scope.Context.Workspaces.SingleAsync()).SecurityAuditCount);
        Assert.Equal(2, await scope.Context.AuditEvents.CountAsync(row => row.Security));
    }

    [Fact]
    public async Task SchemaPatchMergeDeleteAndOldSchemaReceiptPreserveRevisionsAndPermanentIdentity()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var request = Request(list);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        var extra = new ListField(Guid.NewGuid(), "Extra", ListFieldType.Integer);
        var schema = await env.Engine.PublishSchemaAsync(Admin, env.Workspace, list.Id, new(1, "Renamed", [.. list.Fields, extra]));
        Assert.Equal(2, schema.SchemaVersion);
        await Failure(ListFailure.Conflict, () => env.Engine.PublishSchemaAsync(Admin, env.Workspace, list.Id, new(1, "Again", schema.Fields)));
        await Failure(ListFailure.Validation, () => env.Engine.PublishSchemaAsync(Admin, env.Workspace, list.Id,
            new(2, schema.Name, [extra])));
        await Failure(ListFailure.Conflict, () => env.Engine.UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id, new(1, 1, [])));
        var changes = new[] { new FieldChange(list.Fields[0].Id, JsonValue.Create("patched")), new FieldChange(extra.Id, JsonValue.Create(4)) };
        var updated = await env.Engine.UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id, new(1, 2, changes));
        Assert.Equal(2, updated.Revision);
        Assert.Equal(2, updated.SchemaVersion);
        Assert.Equal("patched", updated.Values[list.Fields[0].Id.ToString("D")]!.GetValue<string>());
        await Failure(ListFailure.Conflict, () => env.Engine.UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id, new(1, 2, changes)));
        var preview = await env.Engine.PreviewMergeAsync(Admin, env.Workspace, list.Id, request.Id,
            new(2, request.Values, [new(list.Fields[0].Id, JsonValue.Create("another"))]));
        Assert.True(preview.Fields.Single(field => field.FieldId == list.Fields[0].Id).Conflict);
        await Failure(ListFailure.Conflict, () => env.Engine.DeleteRecordAsync(Admin, env.Workspace, list.Id, request.Id, 1));
        var replay = await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        Assert.True(replay.Replayed);
        Assert.Equal(1, replay.Revision);
        await env.Engine.DeleteRecordAsync(Admin, env.Workspace, list.Id, request.Id, 2);
        await Failure(ListFailure.NotFound, () => env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, request.Id));
        Assert.True((await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request)).Replayed);
        var state = await env.SnapshotAsync(list.Id);
        Assert.Equal(1, state.Claims);
        Assert.Equal(1, state.Identities);
        Assert.Equal(0, state.Records);
        Assert.Equal(2, state.NextNumber);
    }

    [Fact]
    public async Task ReferenceGraphTracksPatchAndExplicitOutgoingDeletionProtectsIncomingTargets()
    {
        var env = await SetupAsync();
        var target = await env.CreateListAsync();
        var a = Request(target);
        var b = Request(target);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, target.Id, a);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, target.Id, b);
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id, ReferenceDisplayFieldId: target.Fields[0].Id);
        var source = await env.Engine.CreateListAsync(Admin, env.Workspace, new(Guid.NewGuid(), "Sources", [reference]));
        var create = new CreateRecordRequest(Guid.NewGuid(), 1, new JsonObject { [reference.Id.ToString("D")] = a.Id.ToString("D") });
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, source.Id, create);
        var label = await env.Engine.ResolveReferenceAsync(Admin, env.Workspace, source.Id, create.Id, reference.Id);
        Assert.Equal(a.Id, label.RecordId);
        Assert.Equal("initial", label.Label!.GetValue<string>());
        await Failure(ListFailure.Conflict, () => env.Engine.DeleteRecordAsync(Admin, env.Workspace, target.Id, a.Id, 1));
        await Failure(ListFailure.Validation, () => env.Engine.CreateRecordAsync(Admin, env.Workspace, source.Id,
            create with { Id = Guid.NewGuid(), Values = new JsonObject { [reference.Id.ToString("D")] = Guid.NewGuid().ToString("D") } }));
        await env.Engine.UpdateRecordAsync(Admin, env.Workspace, source.Id, create.Id,
            new(1, 1, [new(reference.Id, JsonValue.Create(b.Id.ToString("D")))]));
        await env.Engine.DeleteRecordAsync(Admin, env.Workspace, target.Id, a.Id, 1);
        await Failure(ListFailure.Conflict, () => env.Engine.DeleteRecordAsync(Admin, env.Workspace, target.Id, b.Id, 1));
        await env.Engine.DeleteRecordAsync(Admin, env.Workspace, source.Id, create.Id, 2);
        await env.Engine.DeleteRecordAsync(Admin, env.Workspace, target.Id, b.Id, 1);
        await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, env.Workspace);
        Assert.Empty(await scope.Context.RecordReferences.ToListAsync());
    }

    [Fact]
    public async Task ReferenceReadAuthorityAndSchemaDisplayValidationApplyBeforeWrites()
    {
        var env = await SetupAsync();
        var target = await env.CreateListAsync();
        var targetRequest = Request(target);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, target.Id, targetRequest);
        var reference = new ListField(Guid.NewGuid(), "Ref", ListFieldType.Reference, ReferenceListId: target.Id);
        var source = await env.Engine.CreateListAsync(Admin, env.Workspace, new(Guid.NewGuid(), "Source", [reference]));
        await env.Engine.SetMembershipAsync(Admin, env.Workspace, "writer", true, true, false);
        await env.Engine.SetListGrantAsync(Admin, env.Workspace, source.Id, "writer", ListAccess.Read | ListAccess.Write | ListAccess.Design);
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, new JsonObject { [reference.Id.ToString("D")] = targetRequest.Id.ToString("D") });
        var before = await env.SnapshotAsync(source.Id);
        await Failure(ListFailure.NotFound, () => env.Engine.CreateRecordAsync(new("writer"), env.Workspace, source.Id, request));
        await Failure(ListFailure.NotFound, () => env.Engine.CreateListAsync(new("writer"), env.Workspace, new(Guid.NewGuid(), "Denied", [reference])));
        Assert.Equal(before, await env.SnapshotAsync(source.Id));
        await env.Engine.SetListGrantAsync(Admin, env.Workspace, target.Id, "writer", ListAccess.Read);
        await env.Engine.CreateRecordAsync(new("writer"), env.Workspace, source.Id, request);
        await env.Engine.SetListGrantAsync(Admin, env.Workspace, target.Id, "writer", ListAccess.None);
        // Identical retry needs current source Write, but no new target assignment.
        Assert.True((await env.Engine.CreateRecordAsync(new("writer"), env.Workspace, source.Id, request)).Replayed);
        await Failure(ListFailure.NotFound, () => env.Engine.ResolveReferenceAsync(new("writer"), env.Workspace, source.Id, request.Id, reference.Id));
        await Failure(ListFailure.Validation, () => env.Engine.CreateListAsync(Admin, env.Workspace,
            new(Guid.NewGuid(), "Bad display", [reference with { ReferenceDisplayFieldId = Guid.NewGuid() }])));
    }

    [Fact]
    public async Task TemplatesPinProtectedFieldsAndCannotRebindWorkspaceAnchors()
    {
        var env = await SetupAsync();
        var template = new ListTemplate(Guid.NewGuid(), "Pinned", 1, [new(Guid.NewGuid(), "Required", ListFieldType.Text, Required: true)]);
        await SqlServerListSetup.RegisterTemplateAsync(env.AdminOptions, template);
        await SqlServerListSetup.RegisterTemplateAsync(env.AdminOptions, template);
        await Failure(ListFailure.Conflict, () => SqlServerListSetup.RegisterTemplateAsync(env.AdminOptions, template with { Name = "Different" }));
        var list = await env.Engine.CreateListAsync(Admin, env.Workspace, new(Guid.NewGuid(), "Pinned list", [], template.Id, 1));
        Assert.True(list.Fields[0].Protected);
        Assert.Equal(template.Id, list.TemplateId);
        await Failure(ListFailure.Validation, () => env.Engine.PublishSchemaAsync(Admin, env.Workspace, list.Id,
            new(1, list.Name, [list.Fields[0] with { Name = "Altered" }])));
        await Failure(ListFailure.Validation, () => env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, new(Guid.NewGuid(), 1, new JsonObject())));
        await Failure(ListFailure.Conflict, () => SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions,
            env.Workspace, "Host", "policy", "tenant"));
    }

    [Theory]
    [InlineData("patch")]
    [InlineData("schema")]
    [InlineData("delete")]
    [InlineData("membership")]
    [InlineData("grant")]
    public async Task SuppressedMutationAuditRollsBackEveryMutation(string operation)
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var request = Request(list);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        await env.Engine.SetMembershipAsync(Admin, env.Workspace, "writer", true, false, false);
        await env.Engine.SetListGrantAsync(Admin, env.Workspace, list.Id, "writer", ListAccess.Write);
        var before = await env.SnapshotAsync(list.Id);
        var engine = new SqlServerListEngine(env.OptionsWith(new SuppressAuditSave(11)));
        await Failure(ListFailure.Unavailable, () => operation switch
        {
            "patch" => engine.UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id, new(1, 1, [new(list.Fields[0].Id, JsonValue.Create("changed"))])),
            "schema" => engine.PublishSchemaAsync(Admin, env.Workspace, list.Id, new(1, "Changed", list.Fields)),
            "delete" => engine.DeleteRecordAsync(Admin, env.Workspace, list.Id, request.Id, 1),
            "membership" => engine.SetMembershipAsync(Admin, env.Workspace, "writer", false, false, false),
            _ => engine.SetListGrantAsync(Admin, env.Workspace, list.Id, "writer", ListAccess.None)
        });
        Assert.Equal(before, await env.SnapshotAsync(list.Id));
        Assert.Equal(1, (await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, request.Id)).Revision);
        Assert.Equal(1, (await env.Engine.GetListAsync(Admin, env.Workspace, list.Id)).SchemaVersion);
        Assert.True((await env.Engine.CreateRecordAsync(new("writer"), env.Workspace, list.Id,
            Request(list))).Number > 0);
    }

    [Fact]
    public async Task MutationCancellationKnownCommitCleanupAndUncertainOutcomeStayDistinct()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var request = Request(list);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        var before = await env.SnapshotAsync(list.Id);
        using var canceled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqlServerListEngine(env.OptionsWith(new CancelAfterRecordSave(canceled)))
            .UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id,
                new(1, 1, [new(list.Fields[0].Id, JsonValue.Create("canceled"))]), canceled.Token));
        Assert.Equal(before, await env.SnapshotAsync(list.Id));
        await using (var external = new SqlConnection(env.Runtime))
        {
            await external.OpenAsync();
            var reset = new FailReset();
            var options = new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(external).AddInterceptors(reset).Options;
            var accepted = await new SqlServerListEngine(options).UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id,
                new(1, 1, [new(list.Fields[0].Id, JsonValue.Create("accepted"))]));
            Assert.Equal(2, accepted.Revision);
            Assert.True(reset.Triggered);
            Assert.Equal(System.Data.ConnectionState.Closed, external.State);
        }
        var lost = new LoseCommitAcknowledgment();
        await Failure(ListFailure.Unavailable, () => new SqlServerListEngine(env.OptionsWith(lost)).UpdateRecordAsync(Admin, env.Workspace,
            list.Id, request.Id, new(2, 1, [new(list.Fields[0].Id, JsonValue.Create("uncertain"))])));
        Assert.True(lost.Triggered);
        var actual = await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, request.Id);
        Assert.Equal(3, actual.Revision);
        Assert.Equal("uncertain", actual.Values[list.Fields[0].Id.ToString("D")]!.GetValue<string>());
        var committed = await env.SnapshotAsync(list.Id);
        await Failure(ListFailure.Conflict, () => env.Engine.UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id, new(2, 1, [])));
        Assert.Equal(committed, await env.SnapshotAsync(list.Id));
        using var afterCommit = new CancellationTokenSource();
        var completed = await new SqlServerListEngine(env.OptionsWith(new CancelOnRecordCommit(afterCommit)))
            .UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id,
                new(3, 1, [new(list.Fields[0].Id, JsonValue.Create("committed before cancellation"))]), afterCommit.Token);
        Assert.True(afterCommit.IsCancellationRequested);
        Assert.Equal(4, completed.Revision);
        Assert.Equal(0, await UnscopedCountAsync(env.Runtime));
    }

    [Fact]
    public async Task RevocationWaitsForAdmittedMutationThenDeniesTheNextMutation()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var request = Request(list);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        await env.Engine.SetMembershipAsync(Admin, env.Workspace, "writer", true, false, false);
        await env.Engine.SetListGrantAsync(Admin, env.Workspace, list.Id, "writer", ListAccess.Read | ListAccess.Write);
        var pause = new PausePatch();
        var mutation = new SqlServerListEngine(env.OptionsWith(pause)).UpdateRecordAsync(new("writer"), env.Workspace,
            list.Id, request.Id, new(1, 1, [new(list.Fields[0].Id, JsonValue.Create("admitted"))]));
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task revocation;
        try
        {
            var waiting = new ObserveWorkspaceLock();
            revocation = new SqlServerListEngine(env.OptionsWith(waiting)).SetListGrantAsync(Admin, env.Workspace, list.Id, "writer", ListAccess.None);
            await waiting.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(revocation.IsCompleted);
        }
        finally { pause.Release.TrySetResult(); }
        Assert.Equal(2, (await mutation).Revision);
        await revocation;
        await Failure(ListFailure.NotFound, () => env.Engine.UpdateRecordAsync(new("writer"), env.Workspace, list.Id, request.Id, new(2, 1, [])));
        Assert.Equal(2, (await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, request.Id)).Revision);
    }

    [Fact]
    public async Task InterceptorProducedReferencesControlThePersistedGraphAndQuotaRollback()
    {
        var env = await SetupAsync();
        var target = await env.CreateListAsync();
        var a = Request(target);
        var b = Request(target);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, target.Id, a);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, target.Id, b);
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference, ReferenceListId: target.Id);
        var source = await env.Engine.CreateListAsync(Admin, env.Workspace, new(Guid.NewGuid(), "Source", [reference]));
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, new JsonObject { [reference.Id.ToString("D")] = a.Id.ToString("D") });
        var replace = new ReplaceRecordJson { Json = new JsonObject { [reference.Id.ToString("D")] = b.Id.ToString("D") }.ToJsonString() };
        await new SqlServerListEngine(env.OptionsWith(replace)).CreateRecordAsync(Admin, env.Workspace, source.Id, request);
        Assert.Equal(b.Id, (await env.Engine.ResolveReferenceAsync(Admin, env.Workspace, source.Id, request.Id, reference.Id)).RecordId);
        await env.Engine.DeleteRecordAsync(Admin, env.Workspace, target.Id, a.Id, 1);
        await Failure(ListFailure.Conflict, () => env.Engine.DeleteRecordAsync(Admin, env.Workspace, target.Id, b.Id, 1));
        var before = await env.SnapshotAsync(source.Id);
        replace.Json = new JsonObject { [reference.Id.ToString("D")] = Guid.NewGuid().ToString("D") }.ToJsonString();
        await Failure(ListFailure.Validation, () => new SqlServerListEngine(env.OptionsWith(replace))
            .CreateRecordAsync(Admin, env.Workspace, source.Id, request with { Id = Guid.NewGuid() }));
        Assert.Equal(before, await env.SnapshotAsync(source.Id));
        await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, env.Workspace);
        Assert.Equal(b.Id, (await scope.Context.RecordReferences.SingleAsync()).TargetRecordId);
    }

    [Theory]
    [InlineData("builtin")]
    [InlineData("host")]
    [InlineData("template")]
    public async Task SuppressedAdministratorRegistrationNeverReportsSuccess(string operation)
    {
        var env = await SetupAsync();
        var id = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<SqlServerListDbContext>(env.AdminOptions).AddInterceptors(new SuppressAllSaves()).Options;
        await Failure(ListFailure.Unavailable, () => operation switch
        {
            "builtin" => SqlServerListSetup.RegisterWorkspaceAsync(options, id, "Builtin", "owner"),
            "host" => SqlServerListSetup.RegisterHostWorkspaceAsync(options, id, "Host", "policy", "tenant"),
            _ => SqlServerListSetup.RegisterTemplateAsync(options, new(id, "Template", 1, []))
        });
        await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, id);
        Assert.Empty(await scope.Context.Workspaces.ToListAsync());
        Assert.False(await scope.Context.Templates.AnyAsync(row => row.TemplateId == id));
    }

    private sealed class SuppressAllSaves : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(12));
    }

    private sealed class PausePatch : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<RecordRow>().Any(entry => entry.State == EntityState.Modified))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class CancelOnRecordCommit(CancellationTokenSource source) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<RecordRow>().Any()) source.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class SuppressAuditSave(int count) : SaveChangesInterceptor
    {
        public bool Triggered;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuditEventRow>().Any(entry => entry.State == EntityState.Added))
            { Triggered = true; return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(count)); }
            return ValueTask.FromResult(result);
        }
    }
}
