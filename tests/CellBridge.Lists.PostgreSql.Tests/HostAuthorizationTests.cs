using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class HostAuthorizationTests(ListDatabaseFixture database)
{
    private const ListWorkspaceAccess FullWorkspaceAccess = ListWorkspaceAccess.UseLists |
        ListWorkspaceAccess.CreateLists | ListWorkspaceAccess.ManagePermissions | ListWorkspaceAccess.ReadAudit;
    private const ListAccess FullListAccess = ListAccess.Read | ListAccess.Write | ListAccess.Design;

    [PostgreSqlFact]
    public async Task HostPolicyAuthorizesListRecordsSchemasQueriesMergesViewsReferencesAndAuditWithoutAclRows()
    {
        var host = await CreateHostAsync(FullWorkspaceAccess, FullListAccess);
        var label = TextField("Label");
        var target = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Targets", [label]));
        var targetRecordId = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, target.Id,
            new CreateRecordRequest(targetRecordId, 1, Values(label, "Northwind")));

        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id, ReferenceDisplayFieldId: label.Id);
        var source = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Sources", [reference]));
        var sourceRecordId = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, source.Id,
            new CreateRecordRequest(sourceRecordId, 1,
                new JsonObject { [reference.Id.ToString("D")] = targetRecordId.ToString("D") }));

        Assert.Equal(new[] { source.Id, target.Id }.Order().ToArray(),
            (await host.Engine.GetListsAsync(host.Actor, host.Workspace))
                .Select(list => list.Id).Order().ToArray());
        Assert.Equal(target.Id, (await host.Engine.GetListAsync(host.Actor, host.Workspace, target.Id)).Id);
        Assert.Equal(targetRecordId,
            (await host.Engine.GetRecordAsync(host.Actor, host.Workspace, target.Id, targetRecordId)).Id);
        Assert.Equal(targetRecordId,
            Assert.Single((await host.Engine.QueryAsync(host.Actor, host.Workspace, target.Id, new ListQuery())).Records).Id);

        var merge = await host.Engine.PreviewMergeAsync(host.Actor, host.Workspace, target.Id, targetRecordId,
            new MergeRequest(1, Values(label, "Northwind"),
                [new FieldChange(label.Id, JsonValue.Create("Northwind Traders"))]));
        Assert.False(Assert.Single(merge.Fields).Conflict);

        var viewId = Guid.NewGuid();
        await host.Engine.SaveViewAsync(host.Actor, host.Workspace, target.Id,
            new SaveViewRequest(viewId, "All targets", new ListQuery(), [label.Id]));
        Assert.Equal(viewId, Assert.Single(await host.Engine.GetViewsAsync(host.Actor, host.Workspace, target.Id)).Id);

        var resolved = await host.Engine.ResolveReferenceAsync(host.Actor, host.Workspace, source.Id,
            sourceRecordId, reference.Id);
        Assert.Equal("Northwind", resolved.Label!.GetValue<string>());

        var extra = new ListField(Guid.NewGuid(), "Active", ListFieldType.Boolean);
        var published = await host.Engine.PublishSchemaAsync(host.Actor, host.Workspace, target.Id,
            new PublishSchemaRequest(1, target.Name, [label, extra]));
        Assert.Equal(2, published.SchemaVersion);

        var mutableId = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, target.Id,
            new CreateRecordRequest(mutableId, 2, Values(label, "Temporary")));
        var updated = await host.Engine.UpdateRecordAsync(host.Actor, host.Workspace, target.Id, mutableId,
            new UpdateRecordRequest(1, 2, [new FieldChange(label.Id, JsonValue.Create("Updated"))]));
        await host.Engine.DeleteRecordAsync(host.Actor, host.Workspace, target.Id, mutableId, updated.Revision);

        var audit = await host.Engine.GetAuditAsync(host.Actor, host.Workspace);
        Assert.Contains(audit, item => item.Action == "list.create");
        Assert.Contains(audit, item => item.Action == "schema.publish");
        Assert.Contains(audit, item => item.Action == "view.save");
        Assert.Contains(audit, item => item.Action == "record.delete");
        Assert.Equal((0L, 0L), await AclCountsAsync(host.Workspace));
    }

    [PostgreSqlFact]
    public async Task WorkspaceAndListPermissionBitsAreAppliedToEveryOperationClass()
    {
        var host = await CreateHostAsync(FullWorkspaceAccess, FullListAccess);
        var field = TextField("Title");
        var list = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Permission probes", [field]));
        var recordId = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, list.Id,
            new CreateRecordRequest(recordId, 1, Values(field, "original")));
        await host.Engine.SaveViewAsync(host.Actor, host.Workspace, list.Id,
            new SaveViewRequest(Guid.NewGuid(), "Existing", new ListQuery(), [field.Id]));

        host.Policy.Set(Snapshot(host, ListWorkspaceAccess.None, FullListAccess));
        await RejectAsync(() => host.Engine.GetListsAsync(host.Actor, host.Workspace), ListFailure.Forbidden);
        await RejectAsync(() => host.Engine.GetListAsync(host.Actor, host.Workspace, list.Id), ListFailure.Forbidden);

        host.Policy.Set(Snapshot(host, ListWorkspaceAccess.UseLists, ListAccess.Read));
        Assert.Equal(list.Id, (await host.Engine.GetListAsync(host.Actor, host.Workspace, list.Id)).Id);
        Assert.Equal(recordId, (await host.Engine.GetRecordAsync(host.Actor, host.Workspace, list.Id, recordId)).Id);
        Assert.Single((await host.Engine.QueryAsync(host.Actor, host.Workspace, list.Id, new ListQuery())).Records);
        Assert.Single(await host.Engine.GetViewsAsync(host.Actor, host.Workspace, list.Id));
        await RejectAsync(() => host.Engine.PreviewMergeAsync(host.Actor, host.Workspace, list.Id, recordId,
            new MergeRequest(1, Values(field, "original"), [])), ListFailure.NotFound);
        await RejectAsync(() => host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Denied", [TextField("Denied")])), ListFailure.Forbidden);
        await RejectAsync(() => host.Engine.CreateRecordAsync(host.Actor, host.Workspace, list.Id,
            new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "denied"))), ListFailure.NotFound);
        await RejectAsync(() => host.Engine.PublishSchemaAsync(host.Actor, host.Workspace, list.Id,
            new PublishSchemaRequest(1, list.Name, [field])), ListFailure.NotFound);
        await RejectAsync(() => host.Engine.SaveViewAsync(host.Actor, host.Workspace, list.Id,
            new SaveViewRequest(Guid.NewGuid(), "Denied", new ListQuery(), [field.Id])), ListFailure.NotFound);
        await RejectAsync(() => host.Engine.GetAuditAsync(host.Actor, host.Workspace), ListFailure.Forbidden);

        host.Policy.Set(Snapshot(host, ListWorkspaceAccess.UseLists | ListWorkspaceAccess.CreateLists,
            ListAccess.None, new Dictionary<Guid, ListAccess> { [list.Id] = ListAccess.Read }));
        Assert.Equal(list.Id, (await host.Engine.GetListAsync(host.Actor, host.Workspace, list.Id)).Id);
        var createdWithWorkspacePermission = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Created by workspace permission", [TextField("Value")]));

        host.Policy.Set(Snapshot(host, ListWorkspaceAccess.UseLists, ListAccess.None,
            new Dictionary<Guid, ListAccess> { [list.Id] = ListAccess.Write }));
        var writeOnlyRecord = await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, list.Id,
            new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "write only")));
        Assert.Equal(1, writeOnlyRecord.Revision);
        await RejectAsync(() => host.Engine.GetRecordAsync(host.Actor, host.Workspace, list.Id, recordId),
            ListFailure.NotFound);

        host.Policy.Set(Snapshot(host, ListWorkspaceAccess.UseLists, ListAccess.None,
            new Dictionary<Guid, ListAccess> { [list.Id] = ListAccess.Read | ListAccess.Write }));
        var changed = await host.Engine.UpdateRecordAsync(host.Actor, host.Workspace, list.Id, recordId,
            new UpdateRecordRequest(1, 1, [new FieldChange(field.Id, JsonValue.Create("changed"))]));
        Assert.Equal(2, changed.Revision);
        await host.Engine.PreviewMergeAsync(host.Actor, host.Workspace, list.Id, recordId,
            new MergeRequest(1, Values(field, "changed"), []));

        host.Policy.Set(Snapshot(host, ListWorkspaceAccess.UseLists, ListAccess.None,
            new Dictionary<Guid, ListAccess> { [list.Id] = ListAccess.Design }));
        var added = new ListField(Guid.NewGuid(), "Added", ListFieldType.Boolean);
        await host.Engine.PublishSchemaAsync(host.Actor, host.Workspace, list.Id,
            new PublishSchemaRequest(1, list.Name, [field, added]));
        await host.Engine.SaveViewAsync(host.Actor, host.Workspace, list.Id,
            new SaveViewRequest(Guid.NewGuid(), "Designed", new ListQuery(), [field.Id]));

        host.Policy.Set(Snapshot(host, ListWorkspaceAccess.UseLists | ListWorkspaceAccess.ReadAudit,
            ListAccess.None));
        Assert.NotEmpty(await host.Engine.GetAuditAsync(host.Actor, host.Workspace));
        await RejectAsync(() => host.Engine.GetListAsync(host.Actor, host.Workspace, createdWithWorkspacePermission.Id),
            ListFailure.NotFound);
    }

    [PostgreSqlFact]
    public async Task HostPolicyIgnoresLocalAclRowsAndExplicitNoneOverridesDefaultRead()
    {
        var host = await CreateHostAsync(FullWorkspaceAccess, FullListAccess);
        var hiddenField = TextField("Hidden");
        var hidden = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Hidden", [hiddenField]));
        var hiddenRecord = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, hidden.Id,
            new CreateRecordRequest(hiddenRecord, 1, Values(hiddenField, "local ACL must not expose this")));
        var visible = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Visible", [TextField("Visible")]));

        await using (var connection = await database.AdminSource!.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetWorkspaceContextAsync(connection, transaction, host.Workspace);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO cellbridge_lists.memberships(
                  workspace_id,subject,can_create_lists,can_manage_grants)
                VALUES ($1,$2,true,true)
                ON CONFLICT (workspace_id,subject) DO UPDATE
                SET can_create_lists=true,can_manage_grants=true
                """, host.Workspace, host.Actor.Subject);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO cellbridge_lists.list_grants(workspace_id,list_id,subject,access)
                VALUES ($1,$2,$3,$4),($1,$5,$3,$4)
                ON CONFLICT (workspace_id,list_id,subject) DO UPDATE SET access=EXCLUDED.access
                """, host.Workspace, hidden.Id, host.Actor.Subject, (int)FullListAccess, visible.Id);
            await transaction.CommitAsync();
        }

        host.Policy.Set(Snapshot(host, ListWorkspaceAccess.UseLists, ListAccess.Read,
            new Dictionary<Guid, ListAccess> { [hidden.Id] = ListAccess.None }));
        var lists = await host.Engine.GetListsAsync(host.Actor, host.Workspace);
        Assert.Equal(visible.Id, Assert.Single(lists).Id);
        await RejectAsync(() => host.Engine.GetListAsync(host.Actor, host.Workspace, hidden.Id),
            ListFailure.NotFound);
        await RejectAsync(() => host.Engine.GetRecordAsync(host.Actor, host.Workspace, hidden.Id, hiddenRecord),
            ListFailure.NotFound);

        host.Policy.Set(Snapshot(host, ListWorkspaceAccess.UseLists, ListAccess.None));
        Assert.Empty(await host.Engine.GetListsAsync(host.Actor, host.Workspace));
        await RejectAsync(() => host.Engine.GetListAsync(host.Actor, host.Workspace, hidden.Id),
            ListFailure.NotFound);
        await RejectAsync(() => host.Engine.GetRecordAsync(host.Actor, host.Workspace, hidden.Id, hiddenRecord),
            ListFailure.NotFound);

        host.Policy.Set(Snapshot(host, ListWorkspaceAccess.None, FullListAccess));
        await RejectAsync(() => host.Engine.GetListsAsync(host.Actor, host.Workspace), ListFailure.Forbidden);
        await RejectAsync(() => host.Engine.GetListAsync(host.Actor, host.Workspace, hidden.Id),
            ListFailure.Forbidden);
        await RejectAsync(() => host.Engine.GetRecordAsync(host.Actor, host.Workspace, hidden.Id, hiddenRecord),
            ListFailure.Forbidden);
    }

    [PostgreSqlFact]
    public async Task DeniedReferenceDoesNotWaitForTheUnauthorizedTargetRowLock()
    {
        var host = await CreateHostAsync(FullWorkspaceAccess, FullListAccess);
        var label = TextField("Label");
        var target = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Locked target", [label]));
        var targetRecordId = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, target.Id,
            new CreateRecordRequest(targetRecordId, 1, Values(label, "secret")));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id, ReferenceDisplayFieldId: label.Id);
        var source = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Source", [reference]));
        host.Policy.Set(Snapshot(host, ListWorkspaceAccess.UseLists, ListAccess.None,
            new Dictionary<Guid, ListAccess>
            {
                [source.Id] = ListAccess.Write,
                [target.Id] = ListAccess.None
            }));

        await using var connection = await database.AdminSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetWorkspaceContextAsync(connection, transaction, host.Workspace);
        await ExecuteAsync(connection, transaction, """
            SELECT record_id FROM cellbridge_lists.records
            WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3 FOR UPDATE
            """, host.Workspace, target.Id, targetRecordId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var denied = await Assert.ThrowsAsync<ListException>(() => host.Engine.CreateRecordAsync(host.Actor,
            host.Workspace, source.Id, new CreateRecordRequest(Guid.NewGuid(), 1,
                new JsonObject { [reference.Id.ToString("D")] = targetRecordId.ToString("D") }), deadline.Token));
        Assert.Equal(ListFailure.NotFound, denied.Failure);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task HostModeRejectsModeAndDomainMismatchesAndNeverFallsBackToGrantApis()
    {
        var host = await CreateHostAsync(FullWorkspaceAccess, FullListAccess);
        var list = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Host list", [TextField("Value")]));

        var membership = await Assert.ThrowsAsync<ListException>(() => host.Engine.SetMembershipAsync(host.Actor,
            host.Workspace, "another-subject", true, true, true));
        Assert.Equal(ListFailure.NotSupported, membership.Failure);
        var grant = await Assert.ThrowsAsync<ListException>(() => host.Engine.SetListGrantAsync(host.Actor,
            host.Workspace, list.Id, "another-subject", ListAccess.Read));
        Assert.Equal(ListFailure.NotSupported, grant.Failure);
        Assert.Equal((0L, 0L), await AclCountsAsync(host.Workspace));

        var builtInWorkspace = await database.NewWorkspaceAsync("built-in mode mismatch");
        host.Policy.Set(new ListAuthorizationSnapshot(host.Domain, host.Actor.Subject, builtInWorkspace,
            FullWorkspaceAccess, FullListAccess));
        await RejectAsync(() => host.Engine.GetListsAsync(host.Actor, builtInWorkspace), ListFailure.Unavailable);

        host.Policy.Set(Snapshot(host, FullWorkspaceAccess, FullListAccess));
        var wrongDomain = NewDomain();
        var wrongPolicy = new MutablePolicy(new ListAuthorizationSnapshot(wrongDomain, host.Actor.Subject,
            host.Workspace, FullWorkspaceAccess, FullListAccess));
        var wrongProvider = new SingleProcessListAuthorizationProvider(wrongDomain, wrongPolicy.LoadAsync);
        var wrongEngine = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits(), wrongProvider);
        await RejectAsync(() => wrongEngine.GetListsAsync(host.Actor, host.Workspace), ListFailure.Unavailable);

        await RejectAsync(() => database.Engine.GetListsAsync(host.Actor, host.Workspace), ListFailure.Forbidden);
    }

    [PostgreSqlFact]
    public async Task AuthorizationReadCompletesBeforeAnyListMutationStarts()
    {
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host:read-gate:" + Guid.NewGuid().ToString("N"));
        var domain = NewDomain();
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace, "read gate", domain);
        var entered = NewSignal();
        var release = NewSignal();
        var provider = new SingleProcessListAuthorizationProvider(domain, async (requestedActor, requestedWorkspace, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new ListAuthorizationSnapshot(domain, requestedActor.Subject, requestedWorkspace,
                FullWorkspaceAccess, FullListAccess);
        });
        var engine = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits(), provider);
        var listId = Guid.NewGuid();

        var create = engine.CreateListAsync(actor, workspace,
            new CreateListRequest(listId, "Blocked until authorized", [TextField("Value")]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(0L, await CountAsync("cellbridge_lists.lists", workspace));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(listId, (await create).Id);
        Assert.Equal(1L, await CountAsync("cellbridge_lists.lists", workspace));
    }

    [PostgreSqlFact]
    public async Task ExternalOperationsWaitForBothPostgreSqlAuthorizationGates()
    {
        var host = await CreateHostAsync(FullWorkspaceAccess, FullListAccess);

        await using (var connection = await database.AdminSource!.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, transaction,
                "SELECT pg_advisory_xact_lock(391814,hashtext($1))", host.Domain);
            var read = host.Engine.GetListsAsync(host.Actor, host.Workspace);
            await database.WaitForAdvisoryWaitAsync();
            Assert.False(read.IsCompleted);
            await transaction.CommitAsync();
            await read;
        }

        await using (var connection = await database.AdminSource!.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended($1,391813))", host.Workspace.ToString("D"));
            var read = host.Engine.GetListsAsync(host.Actor, host.Workspace);
            await database.WaitForAdvisoryWaitAsync();
            Assert.False(read.IsCompleted);
            await transaction.RollbackAsync();
            await read;
        }
    }

    [PostgreSqlFact]
    public async Task WorkspaceAuthorizationChangesRemainParallelAndDrainUncertainTransactions()
    {
        var domain = NewDomain();
        var actor = new ListActor("host:workspace-drain:" + Guid.NewGuid().ToString("N"));
        var firstWorkspace = Guid.NewGuid();
        var secondWorkspace = Guid.NewGuid();
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, firstWorkspace, "first", domain);
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, secondWorkspace, "second", domain);
        var policy = new MutablePolicy(new ListAuthorizationSnapshot(domain, actor.Subject, firstWorkspace,
            FullWorkspaceAccess, FullListAccess));
        var provider = new SingleProcessListAuthorizationProvider(domain, policy.LoadAsync);
        var engine = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits(), provider);

        foreach (var commit in new[] { true, false })
        {
            await using var connection = await database.AdminSource!.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction,
                "SELECT pg_advisory_xact_lock_shared(hashtextextended($1,391813))", firstWorkspace.ToString("D"));

            var firstCallback = NewSignal();
            var firstChange = engine.CoordinateAuthorizationChangeAsync(firstWorkspace, async _ =>
            {
                Assert.True(await TryAcquireAuthorizationLocksAsync(domain, firstWorkspace));
                firstCallback.TrySetResult();
                return true;
            });
            await database.WaitForAdvisoryWaitAsync();
            Assert.False(firstCallback.Task.IsCompleted);

            var secondChange = await engine.CoordinateAuthorizationChangeAsync(secondWorkspace,
                _ => Task.FromResult("second completed"));
            Assert.Equal("second completed", secondChange);
            Assert.False(firstCallback.Task.IsCompleted);

            if (commit) await transaction.CommitAsync();
            else await transaction.RollbackAsync();
            Assert.True(await firstChange);
            await firstCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [PostgreSqlFact]
    public async Task GlobalAuthorizationDrainWaitsForTransactionStartedByIndependentEngineAndProvider()
    {
        var domain = NewDomain();
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host:global-drain:" + Guid.NewGuid().ToString("N"));
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace, "global drain", domain);
        var snapshot = new ListAuthorizationSnapshot(domain, actor.Subject, workspace,
            FullWorkspaceAccess, FullListAccess);

        await using var firstSource = database.NewRuntimeSource("HostAuthorizationStoreOne");
        await using var secondSource = database.NewRuntimeSource("HostAuthorizationStoreTwo");
        // The independent providers simulate an operation left by an older process. The PostgreSQL
        // domain lock supplies the drain; SingleProcessListAuthorizationProvider remains process-local.
        var firstProvider = new SingleProcessListAuthorizationProvider(domain,
            (_, _, _) => ValueTask.FromResult<ListAuthorizationSnapshot?>(snapshot));
        var secondProvider = new SingleProcessListAuthorizationProvider(domain,
            (_, _, _) => ValueTask.FromResult<ListAuthorizationSnapshot?>(snapshot));
        var firstEngine = new PostgreSqlListEngine(firstSource, new ListLimits(), firstProvider);
        var secondEngine = new PostgreSqlListEngine(secondSource, new ListLimits(), secondProvider);
        await firstEngine.CheckHealthAsync();
        var field = TextField("Value");
        var list = await secondEngine.CreateListAsync(actor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Cross store", [field]));

        await using var blocker = await database.AdminSource!.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await ExecuteAsync(blocker, blockerTransaction,
            "SELECT list_id FROM cellbridge_lists.lists WHERE workspace_id=$1 AND list_id=$2 FOR UPDATE",
            workspace, list.Id);

        var operation = secondEngine.GetListAsync(actor, workspace, list.Id);
        await WaitForDatabaseLockAsync("HostAuthorizationStoreTwo");

        var callbackEntered = NewSignal();
        var change = firstEngine.CoordinateGlobalAuthorizationChangeAsync(_ =>
        {
            callbackEntered.TrySetResult();
            return Task.FromResult(true);
        });
        await database.WaitForAdvisoryWaitAsync();
        Assert.False(callbackEntered.Task.IsCompleted);

        await blockerTransaction.CommitAsync();
        Assert.Equal(list.Id, (await operation).Id);
        Assert.True(await change);
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [PostgreSqlFact]
    public async Task GlobalChangeMakesBlockedOperationsLoadNewDenyPolicyAcrossTwoWorkspaces()
    {
        var domain = NewDomain();
        var actor = new ListActor("host:global-policy:" + Guid.NewGuid().ToString("N"));
        var firstWorkspace = Guid.NewGuid();
        var secondWorkspace = Guid.NewGuid();
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, firstWorkspace,
            "global policy first", domain);
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, secondWorkspace,
            "global policy second", domain);
        var firstAllowed = new ListAuthorizationSnapshot(domain, actor.Subject, firstWorkspace,
            FullWorkspaceAccess, FullListAccess, revision: "allowed");
        var secondAllowed = new ListAuthorizationSnapshot(domain, actor.Subject, secondWorkspace,
            FullWorkspaceAccess, FullListAccess, revision: "allowed");
        var policy = new MutablePolicy(firstAllowed);
        policy.Set(secondAllowed);
        var provider = new SingleProcessListAuthorizationProvider(domain, policy.LoadAsync);
        var engine = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits(), provider);
        var firstField = TextField("First");
        var secondField = TextField("Second");
        var firstList = await engine.CreateListAsync(actor, firstWorkspace,
            new CreateListRequest(Guid.NewGuid(), "First", [firstField]));
        var secondList = await engine.CreateListAsync(actor, secondWorkspace,
            new CreateListRequest(Guid.NewGuid(), "Second", [secondField]));
        var firstRecord = Guid.NewGuid();
        var secondRecord = Guid.NewGuid();
        var callbackEntered = NewSignal();
        var releaseCallback = NewSignal();

        var change = engine.CoordinateGlobalAuthorizationChangeAsync(async _ =>
        {
            policy.Set(new ListAuthorizationSnapshot(domain, actor.Subject, firstWorkspace,
                ListWorkspaceAccess.None, revision: "denied"));
            policy.Set(new ListAuthorizationSnapshot(domain, actor.Subject, secondWorkspace,
                ListWorkspaceAccess.None, revision: "denied"));
            callbackEntered.TrySetResult();
            await releaseCallback.Task;
            return true;
        });
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstWrite = engine.CreateRecordAsync(actor, firstWorkspace, firstList.Id,
            new CreateRecordRequest(firstRecord, 1, Values(firstField, "must not persist")));
        var secondWrite = engine.CreateRecordAsync(actor, secondWorkspace, secondList.Id,
            new CreateRecordRequest(secondRecord, 1, Values(secondField, "must not persist")));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                Task.WhenAll(firstWrite, secondWrite).WaitAsync(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            releaseCallback.TrySetResult();
        }

        Assert.True(await change);
        Assert.Equal(ListFailure.Forbidden, (await Assert.ThrowsAsync<ListException>(() => firstWrite)).Failure);
        Assert.Equal(ListFailure.Forbidden, (await Assert.ThrowsAsync<ListException>(() => secondWrite)).Failure);
        Assert.Equal(0L, await RecordCountAsync(firstWorkspace, firstList.Id, firstRecord));
        Assert.Equal(0L, await RecordCountAsync(secondWorkspace, secondList.Id, secondRecord));
    }

    [PostgreSqlFact]
    public async Task BlockedDatabaseWriteRetainsAuthorizationUntilCommitOrRollback()
    {
        var domain = NewDomain();
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host:write-retention:" + Guid.NewGuid().ToString("N"));
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace,
            "write retention", domain);
        var allowed = new ListAuthorizationSnapshot(domain, actor.Subject, workspace,
            FullWorkspaceAccess, FullListAccess, revision: "allowed");
        var policy = new MutablePolicy(allowed);
        var provider = new SingleProcessListAuthorizationProvider(domain, policy.LoadAsync);
        await using var source = database.NewRuntimeSource("HostAuthorizationBlockedWrite");
        var engine = new PostgreSqlListEngine(source,
            new ListLimits { StatementTimeout = TimeSpan.FromSeconds(10) }, provider);
        var field = TextField("Value");
        var list = await engine.CreateListAsync(actor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Blocked writes", [field]));
        var recordId = Guid.NewGuid();
        await engine.CreateRecordAsync(actor, workspace, list.Id,
            new CreateRecordRequest(recordId, 1, Values(field, "original")));

        await using (var blocker = await database.AdminSource!.OpenConnectionAsync())
        await using (var blockerTransaction = await blocker.BeginTransactionAsync())
        {
            await LockRecordAsync(blocker, blockerTransaction, workspace, list.Id, recordId);
            var update = engine.UpdateRecordAsync(actor, workspace, list.Id, recordId,
                new UpdateRecordRequest(1, 1, [new FieldChange(field.Id, JsonValue.Create("committed"))]));
            await WaitForDatabaseLockAsync("HostAuthorizationBlockedWrite");

            var callbackEntered = NewSignal();
            var change = engine.CoordinateAuthorizationChangeAsync(workspace, _ =>
            {
                policy.Set(new ListAuthorizationSnapshot(domain, actor.Subject, workspace,
                    ListWorkspaceAccess.None, revision: "denied-after-commit"));
                callbackEntered.TrySetResult();
                return Task.FromResult(true);
            });
            try
            {
                await Assert.ThrowsAsync<TimeoutException>(() =>
                    callbackEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(200)));
            }
            finally
            {
                await blockerTransaction.CommitAsync();
            }
            Assert.Equal(2, (await update).Revision);
            Assert.True(await change);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal((2L, "committed"), await ReadStoredRecordAsync(workspace, list.Id, recordId, field.Id));

        policy.Set(new ListAuthorizationSnapshot(domain, actor.Subject, workspace,
            FullWorkspaceAccess, FullListAccess, revision: "allowed-again"));
        await using (var blocker = await database.AdminSource!.OpenConnectionAsync())
        await using (var blockerTransaction = await blocker.BeginTransactionAsync())
        using (var cancellation = new CancellationTokenSource())
        {
            await LockRecordAsync(blocker, blockerTransaction, workspace, list.Id, recordId);
            var update = engine.UpdateRecordAsync(actor, workspace, list.Id, recordId,
                new UpdateRecordRequest(2, 1, [new FieldChange(field.Id, JsonValue.Create("rolled back"))]),
                cancellation.Token);
            await WaitForDatabaseLockAsync("HostAuthorizationBlockedWrite");

            var callbackEntered = NewSignal();
            var change = engine.CoordinateAuthorizationChangeAsync(workspace, _ =>
            {
                policy.Set(new ListAuthorizationSnapshot(domain, actor.Subject, workspace,
                    ListWorkspaceAccess.None, revision: "denied-after-rollback"));
                callbackEntered.TrySetResult();
                return Task.FromResult(true);
            });
            try
            {
                await Assert.ThrowsAsync<TimeoutException>(() =>
                    callbackEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(200)));
            }
            finally
            {
                cancellation.Cancel();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
            Assert.True(await change);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await blockerTransaction.RollbackAsync();
        }
        Assert.Equal((2L, "committed"), await ReadStoredRecordAsync(workspace, list.Id, recordId, field.Id));
    }

    [PostgreSqlFact]
    public async Task CanceledAuthorizationCallbackKeepsItsBarrierUntilTheCallbackSettles()
    {
        var host = await CreateHostAsync(FullWorkspaceAccess, FullListAccess,
            new ListLimits { StatementTimeout = TimeSpan.FromSeconds(10) });
        var callbackEntered = NewSignal();
        var cancellationObserved = NewSignal();
        var settle = NewSignal();
        using var cancellation = new CancellationTokenSource();

        var first = host.Engine.CoordinateAuthorizationChangeAsync(host.Workspace, async ct =>
        {
            callbackEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                await settle.Task;
                throw;
            }
            return true;
        }, cancellation.Token);
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondEntered = NewSignal();
        var second = host.Engine.CoordinateAuthorizationChangeAsync(host.Workspace, _ =>
        {
            secondEntered.TrySetResult();
            return Task.FromResult(true);
        });
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                secondEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            settle.TrySetResult();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(await second);
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [PostgreSqlFact]
    public async Task FailedPostgreSqlDrainDoesNotInvokeTheHostCallback()
    {
        var host = await CreateHostAsync(FullWorkspaceAccess, FullListAccess,
            new ListLimits { StatementTimeout = TimeSpan.FromMilliseconds(150) });
        await using var connection = await database.AdminSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "SELECT pg_advisory_xact_lock_shared(hashtextextended($1,391813))", host.Workspace.ToString("D"));
        var callbackCalled = false;

        var failure = await Assert.ThrowsAsync<ListException>(() => host.Engine.CoordinateAuthorizationChangeAsync(
            host.Workspace, _ =>
            {
                callbackCalled = true;
                return Task.FromResult(true);
            }));
        Assert.Equal(ListFailure.Unavailable, failure.Failure);
        Assert.False(callbackCalled);
        await transaction.RollbackAsync();
    }

    private async Task<HostContext> CreateHostAsync(ListWorkspaceAccess workspaceAccess,
        ListAccess listAccess, ListLimits? limits = null)
    {
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host:test:" + Guid.NewGuid().ToString("N"));
        var domain = NewDomain();
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace,
            "host authorization", domain);
        var policy = new MutablePolicy(new ListAuthorizationSnapshot(domain, actor.Subject, workspace,
            workspaceAccess, listAccess, revision: "initial"));
        var provider = new SingleProcessListAuthorizationProvider(domain, policy.LoadAsync);
        var engine = new PostgreSqlListEngine(database.RuntimeSource!, limits ?? new ListLimits(), provider);
        return new HostContext(workspace, actor, domain, policy, engine);
    }

    private static ListAuthorizationSnapshot Snapshot(HostContext host, ListWorkspaceAccess workspaceAccess,
        ListAccess defaultListAccess, IReadOnlyDictionary<Guid, ListAccess>? overrides = null) =>
        new(host.Domain, host.Actor.Subject, host.Workspace, workspaceAccess, defaultListAccess, overrides,
            Guid.NewGuid().ToString("N"));

    private async Task<(long Memberships, long Grants)> AclCountsAsync(Guid workspace)
    {
        await using var command = database.AdminSource!.CreateCommand("""
            SELECT
              (SELECT count(*) FROM cellbridge_lists.memberships WHERE workspace_id=$1),
              (SELECT count(*) FROM cellbridge_lists.list_grants WHERE workspace_id=$1)
            """);
        command.Parameters.AddWithValue(workspace);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<long> CountAsync(string table, Guid workspace)
    {
        await using var command = database.AdminSource!.CreateCommand(
            $"SELECT count(*) FROM {table} WHERE workspace_id=$1");
        command.Parameters.AddWithValue(workspace);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> RecordCountAsync(Guid workspace, Guid list, Guid record)
    {
        await using var command = database.AdminSource!.CreateCommand("""
            SELECT count(*) FROM cellbridge_lists.records
            WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3
            """);
        command.Parameters.AddWithValue(workspace);
        command.Parameters.AddWithValue(list);
        command.Parameters.AddWithValue(record);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(long Revision, string? Value)> ReadStoredRecordAsync(Guid workspace, Guid list,
        Guid record, Guid field)
    {
        await using var command = database.AdminSource!.CreateCommand("""
            SELECT revision,values_json->>$4 FROM cellbridge_lists.records
            WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3
            """);
        command.Parameters.AddWithValue(workspace);
        command.Parameters.AddWithValue(list);
        command.Parameters.AddWithValue(record);
        command.Parameters.AddWithValue(field.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task LockRecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspace, Guid list, Guid record)
    {
        await SetWorkspaceContextAsync(connection, transaction, workspace);
        await ExecuteAsync(connection, transaction, """
            SELECT record_id FROM cellbridge_lists.records
            WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3 FOR UPDATE
            """, workspace, list, record);
    }

    private async Task WaitForDatabaseLockAsync(string applicationName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = database.AdminSource!.CreateCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname=current_database() AND application_name=$1 AND wait_event_type='Lock'
                """);
            command.Parameters.AddWithValue(applicationName);
            if ((long)(await command.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(20);
        }
        Assert.Fail($"The {applicationName} operation did not reach its database lock wait.");
    }

    private async Task<bool> TryAcquireAuthorizationLocksAsync(string domain, Guid workspace)
    {
        await using var connection = await database.AdminSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT pg_try_advisory_xact_lock(391814,hashtext($1))
              AND pg_try_advisory_xact_lock(hashtextextended($2,391813))
            """, connection, transaction);
        command.Parameters.AddWithValue(domain);
        command.Parameters.AddWithValue(workspace.ToString("D"));
        var acquired = (bool)(await command.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();
        return acquired;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetWorkspaceContextAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspace)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction);
        command.Parameters.AddWithValue(workspace.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RejectAsync(Func<Task> operation, ListFailure expected)
    {
        var failure = await Assert.ThrowsAsync<ListException>(operation);
        Assert.Equal(expected, failure.Failure);
    }

    private static async Task RejectAsync<T>(Func<Task<T>> operation, ListFailure expected)
    {
        var failure = await Assert.ThrowsAsync<ListException>(operation);
        Assert.Equal(expected, failure.Failure);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string NewDomain() => "host-tests:" + Guid.NewGuid().ToString("N");

    private static ListField TextField(string name) =>
        new(Guid.NewGuid(), name, ListFieldType.Text, MaxLength: 200);

    private static JsonObject Values(ListField field, string value) =>
        new() { [field.Id.ToString("D")] = value };

    private sealed record HostContext(Guid Workspace, ListActor Actor, string Domain,
        MutablePolicy Policy, PostgreSqlListEngine Engine);

    private sealed class MutablePolicy
    {
        private readonly ConcurrentDictionary<Guid, ListAuthorizationSnapshot> _snapshots = new();

        public MutablePolicy(ListAuthorizationSnapshot initial) => Set(initial);

        public void Set(ListAuthorizationSnapshot snapshot) => _snapshots[snapshot.WorkspaceId] = snapshot;

        public ValueTask<ListAuthorizationSnapshot?> LoadAsync(ListActor actor, Guid workspaceId, CancellationToken _)
        {
            _snapshots.TryGetValue(workspaceId, out var snapshot);
            return ValueTask.FromResult<ListAuthorizationSnapshot?>(
                snapshot?.Subject == actor.Subject ? snapshot : null);
        }
    }
}
