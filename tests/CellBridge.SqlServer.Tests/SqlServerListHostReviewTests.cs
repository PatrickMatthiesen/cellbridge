using System.Data.Common;
using System.Text;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

public sealed partial class SqlServerListEngineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostReferenceReplayAndLostAcknowledgmentResolutionDoNotRequireRevokedTargetRead(bool loseAcknowledgment)
    {
        var env = await SetupAsync();
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host", "tenant");
        var policy = new HostPolicy("policy");
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", "policy", "tenant");
        var engine = new SqlServerListEngine(env.RuntimeOptions, null, policy);
        var target = await engine.CreateListAsync(actor, workspace, NewListRequest());
        var targetRequest = Request(target);
        await engine.CreateRecordAsync(actor, workspace, target.Id, targetRequest);
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference, ReferenceListId: target.Id);
        var source = await engine.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Source", [reference]));
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, new JsonObject { [reference.Id.ToString("D")] = targetRequest.Id.ToString("D") });
        if (loseAcknowledgment)
            await Failure(ListFailure.Unavailable, () => new SqlServerListEngine(env.OptionsWith(new LoseCommitAcknowledgment()), null, policy)
                .CreateRecordAsync(actor, workspace, source.Id, request));
        else await engine.CreateRecordAsync(actor, workspace, source.Id, request);
        var before = await WorkspaceSnapshotAsync(env, workspace, source.Id);
        policy.Requests.Clear();
        policy.Allow = decision => decision.ListId != target.Id;
        var replay = await engine.CreateRecordAsync(actor, workspace, source.Id, request);
        Assert.Equal(new CreateRecordResult(request.Id, 1, 1, true), replay);
        Assert.DoesNotContain(policy.Requests, decision => decision.ListId == target.Id);
        Assert.Equal(before, await WorkspaceSnapshotAsync(env, workspace, source.Id));
        await Failure(ListFailure.Conflict, () => engine.CreateRecordAsync(actor with { Subject = "another" }, workspace, source.Id, request));
        await Failure(ListFailure.Conflict, () => engine.CreateRecordAsync(actor, workspace, source.Id, request with { SchemaVersion = 2 }));
        await Failure(ListFailure.NotFound, () => engine.CreateRecordAsync(actor, workspace, source.Id, request with { Id = Guid.NewGuid() }));
        policy.Allow = decision => decision.ListId != target.Id && !(decision.ListId == source.Id && decision.Action == ListAuthorizationAction.Write);
        await Failure(ListFailure.NotFound, () => engine.CreateRecordAsync(actor, workspace, source.Id, request));
        Assert.Equal(before, await WorkspaceSnapshotAsync(env, workspace, source.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReceiptThatExpiresOrIsPrunedAfterHostPreflightCannotAdmitAFreshCreate(bool delete)
    {
        var env = await SetupAsync();
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host", "tenant");
        var policy = new HostPolicy("policy");
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", "policy", "tenant");
        var engine = new SqlServerListEngine(env.RuntimeOptions, null, policy);
        var target = await engine.CreateListAsync(actor, workspace, NewListRequest());
        var targetRequest = Request(target);
        await engine.CreateRecordAsync(actor, workspace, target.Id, targetRequest);
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference, ReferenceListId: target.Id);
        var source = await engine.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Source", [reference]));
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, new JsonObject { [reference.Id.ToString("D")] = targetRequest.Id.ToString("D") });
        await engine.CreateRecordAsync(actor, workspace, source.Id, request);
        var before = await WorkspaceSnapshotAsync(env, workspace, source.Id);
        var bytes = 192L + Encoding.UTF8.GetByteCount(actor.Subject) + Encoding.UTF8.GetByteCount(ListValidation.CreateDigest(request));
        policy.Allow = decision => decision.ListId != target.Id;
        var hook = new AfterReceiptPreflight(async () =>
        {
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, workspace);
            var receipt = await scope.Context.CreateReceipts.SingleAsync(row => row.ListId == source.Id);
            if (delete)
            {
                scope.Context.CreateReceipts.Remove(receipt);
                (await scope.Context.Workspaces.SingleAsync()).StoredBytes -= bytes;
            }
            else receipt.ExpiresUtc = receipt.CreatedUtc.AddTicks(-1);
            await scope.Context.SaveChangesAsync();
            await scope.CommitAsync();
        });
        var retry = new SqlServerListEngine(env.OptionsWith(new ArmReceiptPreflight(hook), new RunReceiptPreflight(hook)), null, policy);
        await Failure(ListFailure.Conflict, () => retry.CreateRecordAsync(actor, workspace, source.Id, request));
        Assert.True(hook.Triggered);
        Assert.Equal(before with { Bytes = before.Bytes - bytes, Receipts = 0 }, await WorkspaceSnapshotAsync(env, workspace, source.Id));
    }

    [Fact]
    public async Task HostMalformedReferencePublicationValidatesAfterSourceAuthorityWithoutWrites()
    {
        var schemaReads = new SchemaReads();
        var env = await SetupAsync();
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host", "tenant");
        var policy = new HostPolicy("policy");
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", "policy", "tenant");
        var engine = new SqlServerListEngine(env.OptionsWith(schemaReads), null, policy);
        var source = await engine.CreateListAsync(actor, workspace, NewListRequest());
        var before = await WorkspaceSnapshotAsync(env, workspace, source.Id);
        var bad = new ListField(Guid.NewGuid(), "Missing target", ListFieldType.Reference);
        var publication = new PublishSchemaRequest(1, source.Name, [.. source.Fields, bad]);
        await Failure(ListFailure.Validation, () => engine.PublishSchemaAsync(actor, workspace, source.Id, publication));
        Assert.Equal(before, await WorkspaceSnapshotAsync(env, workspace, source.Id));
        policy.Allow = decision => decision.Action != ListAuthorizationAction.Design;
        schemaReads.Count = 0;
        await Failure(ListFailure.NotFound, () => engine.PublishSchemaAsync(actor, workspace, source.Id, publication));
        Assert.Equal(0, schemaReads.Count);
        Assert.Equal(before, await WorkspaceSnapshotAsync(env, workspace, source.Id));
        await using var check = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, workspace);
        Assert.Single(await check.Context.ListSchemas.ToListAsync());
        Assert.Equal(1, (await check.Context.Lists.SingleAsync()).SchemaVersion);
    }

    private static async Task<Snapshot> WorkspaceSnapshotAsync(Environment env, Guid workspace, Guid listId)
    {
        await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, workspace);
        var db = scope.Context;
        var row = await db.Workspaces.SingleAsync();
        var counter = await db.ListCounters.SingleAsync(value => value.ListId == listId);
        return new(row.StoredBytes, row.AuditCount, counter.IdentityCount, counter.NextNumber,
            await db.Records.CountAsync(value => value.ListId == listId), await db.RecordTombstones.CountAsync(value => value.ListId == listId),
            await db.CreateReceipts.CountAsync(value => value.ListId == listId));
    }

    private sealed class AfterReceiptPreflight(Func<Task> action)
    {
        public DbContext? Context;
        public bool Triggered;
        public Task InvokeAsync() { Triggered = true; return action(); }
    }
    private sealed class ArmReceiptPreflight(AfterReceiptPreflight hook) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!hook.Triggered && command.CommandText.Contains("FROM [cellbridge_lists].[create_receipts]", StringComparison.Ordinal)) hook.Context = eventData.Context;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class RunReceiptPreflight(AfterReceiptPreflight hook) : DbTransactionInterceptor
    {
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (!hook.Triggered && ReferenceEquals(hook.Context, eventData.Context)) await hook.InvokeAsync();
        }
    }
    private sealed class SchemaReads : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[cellbridge_lists].[list_schemas]", StringComparison.Ordinal)) Count++;
            return ValueTask.FromResult(result);
        }
    }
}
