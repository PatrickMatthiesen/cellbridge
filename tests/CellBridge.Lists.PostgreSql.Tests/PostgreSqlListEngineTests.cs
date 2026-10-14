using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[CollectionDefinition("lists-postgresql", DisableParallelization = true)]
public sealed class ListsPostgreSqlCollection : ICollectionFixture<ListDatabaseFixture>;

[Collection("lists-postgresql")]
public sealed class PostgreSqlListEngineTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task RuntimeRoleHasNoDdlOrRlsBypassAndMissingContextReturnsNoRows()
    {
        await database.Engine.CheckHealthAsync();
        await using var connection = await database.RuntimeSource!.OpenConnectionAsync();
        await using var privilege = new NpgsqlCommand("""
            SELECT r.rolsuper,r.rolbypassrls,has_schema_privilege(current_user,'cellbridge_lists','CREATE'),
              pg_has_role(current_user,c.relowner,'MEMBER')
            FROM pg_roles r,pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE r.rolname=current_user AND n.nspname='cellbridge_lists' AND c.relname='records'
            """, connection);
        await using (var reader = await privilege.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
            Assert.False(reader.GetBoolean(2));
            Assert.False(reader.GetBoolean(3));
        }
        await using var count = new NpgsqlCommand("SELECT count(*) FROM cellbridge_lists.memberships", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var ddl = new NpgsqlCommand("CREATE TABLE cellbridge_lists.forbidden_test(id integer)", connection);
            await ddl.ExecuteNonQueryAsync();
        });
    }

    [PostgreSqlFact]
    public async Task ListsAndRecordsAreIsolatedAndCreateRetriesReturnOriginalResult()
    {
        var first = await database.NewWorkspaceAsync("first");
        var second = await database.NewWorkspaceAsync("second");
        var field = TextField();
        var firstList = await database.Engine.CreateListAsync(database.AdminActor, first,
            new CreateListRequest(Guid.NewGuid(), "Customers", [field]));
        var secondList = await database.Engine.CreateListAsync(database.AdminActor, second,
            new CreateListRequest(Guid.NewGuid(), "Customers", [field]));
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "Ada"));
        var created = await database.Engine.CreateRecordAsync(database.AdminActor, first, firstList.Id, request);
        var replay = await database.Engine.CreateRecordAsync(database.AdminActor, first, firstList.Id, request);
        Assert.True(replay.Replayed);
        Assert.Equal((created.Id, created.Revision, created.Number), (replay.Id, replay.Revision, replay.Number));
        Assert.Empty((await database.Engine.QueryAsync(database.AdminActor, second, secondList.Id, new ListQuery())).Records);
        var hidden = await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.GetListAsync(database.AdminActor, second, firstList.Id));
        Assert.Equal(ListFailure.NotFound, hidden.Failure);

        await using var connection = await database.RuntimeSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction))
        {
            context.Parameters.AddWithValue(second.ToString("D"));
            await context.ExecuteNonQueryAsync();
        }
        await using var direct = new NpgsqlCommand("SELECT count(*) FROM cellbridge_lists.records WHERE workspace_id=$1", connection, transaction);
        direct.Parameters.AddWithValue(first);
        Assert.Equal(0L, await direct.ExecuteScalarAsync());
    }

    [PostgreSqlFact]
    public async Task MembershipAndListGrantAreBothRequiredAfterRevocation()
    {
        var workspace = await database.NewWorkspaceAsync("revocation");
        var field = TextField();
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Restricted", [field]));
        var reader = new ListActor("test:reader:" + Guid.NewGuid().ToString("N"));
        await database.Engine.SetMembershipAsync(database.AdminActor, workspace, reader.Subject, true, false, false);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id, reader.Subject, ListAccess.Read);
        Assert.Equal(list.Id, (await database.Engine.GetListAsync(reader, workspace, list.Id)).Id);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id, reader.Subject, ListAccess.None);
        Assert.Equal(ListFailure.NotFound, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.GetListAsync(reader, workspace, list.Id))).Failure);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id, reader.Subject, ListAccess.Read);
        await database.Engine.SetMembershipAsync(database.AdminActor, workspace, reader.Subject, false, false, false);
        Assert.Equal(ListFailure.Forbidden, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.GetListAsync(reader, workspace, list.Id))).Failure);
    }

    [PostgreSqlFact]
    public async Task NumbersNeverReuseAndReferenceDeletionIsRestricted()
    {
        var workspace = await database.NewWorkspaceAsync("references");
        var label = TextField();
        var target = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Companies", [label], NumberPrefix: "C"));
        var targetId = Guid.NewGuid();
        var first = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, target.Id,
            new CreateRecordRequest(targetId, 1, Values(label, "Northwind")));
        var reference = new ListField(Guid.NewGuid(), "Company", ListFieldType.Reference, ReferenceListId: target.Id,
            ReferenceDisplayFieldId: label.Id);
        var source = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Contacts", [reference]));
        var sourceId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, source.Id,
            new CreateRecordRequest(sourceId, 1, new JsonObject { [reference.Id.ToString("D")] = targetId.ToString("D") }));
        var resolved = await database.Engine.ResolveReferenceAsync(database.AdminActor, workspace, source.Id, sourceId, reference.Id);
        Assert.Equal("Northwind", resolved.Label!.GetValue<string>());
        Assert.Equal(ListFailure.Conflict, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.DeleteRecordAsync(database.AdminActor, workspace, target.Id, targetId, 1))).Failure);
        await database.Engine.DeleteRecordAsync(database.AdminActor, workspace, source.Id, sourceId, 1);
        await database.Engine.DeleteRecordAsync(database.AdminActor, workspace, target.Id, targetId, 1);
        var second = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, target.Id,
            new CreateRecordRequest(Guid.NewGuid(), 1, Values(label, "Contoso")));
        Assert.True(second.Number > first.Number);
        Assert.Equal(ListFailure.Conflict, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.CreateRecordAsync(database.AdminActor, workspace, target.Id,
                new CreateRecordRequest(targetId, 1, Values(label, "Reused"))))).Failure);
    }

    [PostgreSqlFact]
    public async Task TemplateFieldsStayProtectedAndSchemasPersistAcrossEngineInstances()
    {
        var templateField = TextField();
        var template = new ListTemplate(Guid.NewGuid(), "Template X", 1, [templateField]);
        await PostgreSqlListSetup.RegisterTemplateAsync(database.AdminSource!, template);
        var workspace = await database.NewWorkspaceAsync("template");
        var extension = new ListField(Guid.NewGuid(), "Optional", ListFieldType.Integer);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "X", [extension], template.Id, template.Version));
        Assert.True(list.Fields.Single(x => x.Id == templateField.Id).Protected);
        var changed = list.Fields.Select(x => x.Id == templateField.Id ? x with { Name = "Changed" } : x).ToArray();
        Assert.Equal(ListFailure.Validation, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.PublishSchemaAsync(database.AdminActor, workspace, list.Id,
                new PublishSchemaRequest(1, list.Name, changed)))).Failure);
        var restarted = new PostgreSqlListEngine(database.RuntimeSource!);
        Assert.Equal(list.Id, (await restarted.GetListAsync(database.AdminActor, workspace, list.Id)).Id);
    }

    [PostgreSqlFact]
    public async Task CreateReceiptsSurviveSchemaEditDeletionAndRevocationChecksComeFirst()
    {
        var workspace = await database.NewWorkspaceAsync("receipts");
        var field = TextField();
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Requests", [field]));
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "original"));
        var created = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
        await database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, request.Id,
            new UpdateRecordRequest(1, 1, [new FieldChange(field.Id, JsonValue.Create("edited"))]));
        var extension = new ListField(Guid.NewGuid(), "Optional", ListFieldType.Boolean);
        await database.Engine.PublishSchemaAsync(database.AdminActor, workspace, list.Id,
            new PublishSchemaRequest(1, list.Name, [field, extension]));
        await database.Engine.DeleteRecordAsync(database.AdminActor, workspace, list.Id, request.Id, 2);
        var replay = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
        Assert.True(replay.Replayed);
        Assert.Equal((created.Id, created.Revision, created.Number), (replay.Id, replay.Revision, replay.Number));

        var other = new ListActor("test:receipt:" + Guid.NewGuid().ToString("N"));
        await database.Engine.SetMembershipAsync(database.AdminActor, workspace, other.Subject, true, false, false);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id, other.Subject, ListAccess.Write);
        Assert.Equal(ListFailure.Conflict, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.CreateRecordAsync(other, workspace, list.Id, request))).Failure);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id, other.Subject, ListAccess.None);
        Assert.Equal(ListFailure.NotFound, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.CreateRecordAsync(other, workspace, list.Id, request))).Failure);

        await using (var expire = database.AdminSource!.CreateCommand("UPDATE cellbridge_lists.create_receipts SET expires_utc=clock_timestamp()-interval '1 second' WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3"))
        {
            expire.Parameters.AddWithValue(workspace);
            expire.Parameters.AddWithValue(list.Id);
            expire.Parameters.AddWithValue(request.Id);
            await expire.ExecuteNonQueryAsync();
        }
        Assert.Equal(ListFailure.Conflict, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request))).Failure);
    }

    [PostgreSqlFact]
    public async Task ConcurrentUpdatesAndSchemaPublicationsHaveOneWinner()
    {
        var workspace = await database.NewWorkspaceAsync("concurrency");
        var field = TextField();
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Concurrent", [field]));
        var recordId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new CreateRecordRequest(recordId, 1, Values(field, "start")));
        var updates = new[] { "one", "two" }.Select(value => CaptureAsync(() =>
            database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, recordId,
                new UpdateRecordRequest(1, 1, [new FieldChange(field.Id, JsonValue.Create(value))])))).ToArray();
        await Task.WhenAll(updates);
        Assert.Equal(1, updates.Count(x => x.Result is null));
        Assert.Equal(1, updates.Count(x => x.Result?.Failure == ListFailure.Conflict));

        var schemas = new[] { "A", "B" }.Select(name => CaptureAsync(() =>
            database.Engine.PublishSchemaAsync(database.AdminActor, workspace, list.Id,
                new PublishSchemaRequest(1, name, [field, new ListField(Guid.NewGuid(), name, ListFieldType.Boolean)])))).ToArray();
        await Task.WhenAll(schemas);
        Assert.Equal(1, schemas.Count(x => x.Result is null));
        Assert.Equal(1, schemas.Count(x => x.Result?.Failure == ListFailure.Conflict));
    }

    [PostgreSqlFact]
    public async Task ReferenceAccessIsRecheckedAndOrdinaryQuotaDoesNotBlockRevocation()
    {
        var workspace = await database.NewWorkspaceAsync("authority");
        var label = TextField();
        var target = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Targets", [label]));
        var targetId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, target.Id,
            new CreateRecordRequest(targetId, 1, Values(label, "secret label")));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id, ReferenceDisplayFieldId: label.Id);
        var source = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Sources", [reference]));
        var sourceId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, source.Id,
            new CreateRecordRequest(sourceId, 1, new JsonObject { [reference.Id.ToString("D")] = targetId.ToString("D") }));
        var subject = new ListActor("test:limited:" + Guid.NewGuid().ToString("N"));
        await database.Engine.SetMembershipAsync(database.AdminActor, workspace, subject.Subject, true, false, false);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, source.Id, subject.Subject, ListAccess.Read | ListAccess.Write);
        Assert.Equal(ListFailure.NotFound, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.CreateRecordAsync(subject, workspace, source.Id,
                new CreateRecordRequest(Guid.NewGuid(), 1, new JsonObject { [reference.Id.ToString("D")] = targetId.ToString("D") })))).Failure);
        Assert.Equal(ListFailure.NotFound, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.ResolveReferenceAsync(subject, workspace, source.Id, sourceId, reference.Id))).Failure);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, target.Id, subject.Subject, ListAccess.Read);
        Assert.Equal("secret label", (await database.Engine.ResolveReferenceAsync(subject, workspace, source.Id, sourceId, reference.Id)).Label!.GetValue<string>());

        var numberReference = new ListField(Guid.NewGuid(), "Target number", ListFieldType.Reference,
            ReferenceListId: target.Id);
        var numberSource = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Number sources", [numberReference]));
        var numberSourceId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, numberSource.Id,
            new CreateRecordRequest(numberSourceId, 1, new JsonObject { [numberReference.Id.ToString("D")] = targetId.ToString("D") }));
        var numberLabel = await database.Engine.ResolveReferenceAsync(database.AdminActor, workspace, numberSource.Id, numberSourceId, numberReference.Id);
        Assert.Null(numberLabel.Label);
        Assert.True(numberLabel.Number > 0);

        var otherWorkspace = await database.NewWorkspaceAsync("cross-reference");
        var crossReference = new ListField(Guid.NewGuid(), "Cross", ListFieldType.Reference,
            ReferenceListId: target.Id);
        Assert.Equal(ListFailure.NotFound, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.CreateListAsync(database.AdminActor, otherWorkspace,
                new CreateListRequest(Guid.NewGuid(), "Invalid cross reference", [crossReference])))).Failure);

        var raceTargetId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, target.Id,
            new CreateRecordRequest(raceTargetId, 1, Values(label, "race")));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ListException?> AssignAfterGate()
        {
            await gate.Task;
            return await CaptureAsync(() => database.Engine.CreateRecordAsync(database.AdminActor, workspace, source.Id,
                new CreateRecordRequest(Guid.NewGuid(), 1, new JsonObject { [reference.Id.ToString("D")] = raceTargetId.ToString("D") })));
        }
        async Task<ListException?> DeleteAfterGate()
        {
            await gate.Task;
            return await CaptureAsync(() => database.Engine.DeleteRecordAsync(database.AdminActor, workspace, target.Id, raceTargetId, 1));
        }
        var assignmentRace = AssignAfterGate();
        var deletionRace = DeleteAfterGate();
        gate.SetResult();
        await Task.WhenAll(assignmentRace, deletionRace);
        Assert.True((assignmentRace.Result is null && deletionRace.Result?.Failure == ListFailure.Conflict) ||
            (deletionRace.Result is null && assignmentRace.Result?.Failure == ListFailure.Validation));

        await database.SetWorkspaceContextAsync(workspace, async (connection, transaction) =>
        {
            await using var fill = new NpgsqlCommand("UPDATE cellbridge_lists.workspaces SET stored_bytes=67108864 WHERE workspace_id=$1", connection, transaction);
            fill.Parameters.AddWithValue(workspace);
            await fill.ExecuteNonQueryAsync();
        });
        var quotaEngine = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits
        {
            MaxWorkspaceBytes = 67108864
        });
        await quotaEngine.SetListGrantAsync(database.AdminActor, workspace, target.Id, subject.Subject, ListAccess.None);
        Assert.Equal(ListFailure.Limit, (await Assert.ThrowsAsync<ListException>(() =>
            quotaEngine.SetListGrantAsync(database.AdminActor, workspace, target.Id, subject.Subject, ListAccess.Read))).Failure);
        var audit = await quotaEngine.GetAuditAsync(database.AdminActor, workspace);
        Assert.Contains(audit, x => x.Action == "grant.set" && x.TargetSubject == subject.Subject);
    }

    [PostgreSqlFact]
    public async Task AuthorizationLocksOrderOperationsOnBothSidesOfGrantRevocation()
    {
        var workspace = await database.NewWorkspaceAsync("revocation-race");
        var field = TextField();
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Race", [field]));
        var subject = new ListActor("test:race:" + Guid.NewGuid().ToString("N"));
        await database.Engine.SetMembershipAsync(database.AdminActor, workspace, subject.Subject, true, false, false);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id, subject.Subject, ListAccess.Read);

        await using (var connection = await database.RuntimeSource!.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetContextAndLockAsync(connection, transaction, workspace, exclusive: false);
            await using (var authorize = new NpgsqlCommand("SELECT access FROM cellbridge_lists.list_grants WHERE workspace_id=$1 AND list_id=$2 AND subject=$3", connection, transaction))
            {
                authorize.Parameters.AddWithValue(workspace);
                authorize.Parameters.AddWithValue(list.Id);
                authorize.Parameters.AddWithValue(subject.Subject);
                Assert.Equal((int)ListAccess.Read, await authorize.ExecuteScalarAsync());
            }
            var revocation = database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id, subject.Subject, ListAccess.None);
            await database.WaitForAdvisoryWaitAsync();
            await using (var protectedRead = new NpgsqlCommand("SELECT name FROM cellbridge_lists.lists WHERE workspace_id=$1 AND list_id=$2", connection, transaction))
            {
                protectedRead.Parameters.AddWithValue(workspace);
                protectedRead.Parameters.AddWithValue(list.Id);
                Assert.Equal("Race", await protectedRead.ExecuteScalarAsync());
            }
            await transaction.CommitAsync();
            await revocation;
        }
        Assert.Equal(ListFailure.NotFound, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.GetListAsync(subject, workspace, list.Id))).Failure);

        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id, subject.Subject, ListAccess.Read);
        await using (var connection = await database.RuntimeSource!.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetContextAndLockAsync(connection, transaction, workspace, exclusive: true);
            var read = database.Engine.GetListAsync(subject, workspace, list.Id);
            await database.WaitForAdvisoryWaitAsync();
            await using var remove = new NpgsqlCommand("DELETE FROM cellbridge_lists.list_grants WHERE workspace_id=$1 AND list_id=$2 AND subject=$3", connection, transaction);
            remove.Parameters.AddWithValue(workspace);
            remove.Parameters.AddWithValue(list.Id);
            remove.Parameters.AddWithValue(subject.Subject);
            await remove.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            Assert.Equal(ListFailure.NotFound, (await Assert.ThrowsAsync<ListException>(() => read)).Failure);
        }

        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id, subject.Subject, ListAccess.Read);
        await using (var connection = await database.RuntimeSource!.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetContextAndLockAsync(connection, transaction, workspace, exclusive: false);
            await using (var authorize = new NpgsqlCommand("SELECT 1 FROM cellbridge_lists.memberships WHERE workspace_id=$1 AND subject=$2", connection, transaction))
            {
                authorize.Parameters.AddWithValue(workspace);
                authorize.Parameters.AddWithValue(subject.Subject);
                Assert.Equal(1, await authorize.ExecuteScalarAsync());
            }
            var revocation = database.Engine.SetMembershipAsync(database.AdminActor, workspace, subject.Subject, false, false, false);
            await database.WaitForAdvisoryWaitAsync();
            await using (var protectedRead = new NpgsqlCommand("SELECT name FROM cellbridge_lists.lists WHERE workspace_id=$1 AND list_id=$2", connection, transaction))
            {
                protectedRead.Parameters.AddWithValue(workspace);
                protectedRead.Parameters.AddWithValue(list.Id);
                Assert.Equal("Race", await protectedRead.ExecuteScalarAsync());
            }
            await transaction.CommitAsync();
            await revocation;
        }
        Assert.Equal(ListFailure.Forbidden, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.GetListAsync(subject, workspace, list.Id))).Failure);

        await database.Engine.SetMembershipAsync(database.AdminActor, workspace, subject.Subject, true, false, false);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id, subject.Subject, ListAccess.Read);
        await using (var connection = await database.RuntimeSource!.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetContextAndLockAsync(connection, transaction, workspace, exclusive: true);
            var read = database.Engine.GetListAsync(subject, workspace, list.Id);
            await database.WaitForAdvisoryWaitAsync();
            await using var remove = new NpgsqlCommand("DELETE FROM cellbridge_lists.memberships WHERE workspace_id=$1 AND subject=$2", connection, transaction);
            remove.Parameters.AddWithValue(workspace);
            remove.Parameters.AddWithValue(subject.Subject);
            await remove.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            Assert.Equal(ListFailure.Forbidden, (await Assert.ThrowsAsync<ListException>(() => read)).Failure);
        }
    }

    [PostgreSqlFact]
    public async Task ReplacingAViewChargesOnlyItsLogicalSizeDifference()
    {
        var workspace = await database.NewWorkspaceAsync("view-accounting");
        var field = TextField();
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Views", [field]));
        var viewId = Guid.NewGuid();
        var request = new SaveViewRequest(viewId, "A", new ListQuery(), [field.Id]);
        await database.Engine.SaveViewAsync(database.AdminActor, workspace, list.Id, request);
        var afterCreate = await database.GetWorkspaceBytesAsync(workspace);

        await database.Engine.SaveViewAsync(database.AdminActor, workspace, list.Id, request);
        var afterSame = await database.GetWorkspaceBytesAsync(workspace);
        var auditBytes = 96 + System.Text.Encoding.UTF8.GetByteCount(database.AdminActor.Subject) +
            System.Text.Encoding.UTF8.GetByteCount("view.save") + 16;
        Assert.Equal(auditBytes, afterSame - afterCreate);

        var larger = request with { Name = new string('L', 101) };
        await database.Engine.SaveViewAsync(database.AdminActor, workspace, list.Id, larger);
        var afterLarger = await database.GetWorkspaceBytesAsync(workspace);
        Assert.Equal(auditBytes + 100, afterLarger - afterSame);

        await database.Engine.SaveViewAsync(database.AdminActor, workspace, list.Id, request);
        var afterSmaller = await database.GetWorkspaceBytesAsync(workspace);
        Assert.Equal(auditBytes - 100, afterSmaller - afterLarger);
    }

    [PostgreSqlFact]
    public async Task NullCharactersAreRejectedBeforePostgreSqlJsonAndAuthorityWrites()
    {
        var workspace = await database.NewWorkspaceAsync("null-validation");
        var field = TextField();
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Null validation", [field]));
        var recordId = Guid.NewGuid();
        Assert.Equal(ListFailure.Validation, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
                new CreateRecordRequest(recordId, 1, Values(field, "bad\0value"))))).Failure);
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new CreateRecordRequest(recordId, 1, Values(field, "valid")));
        Assert.Equal(ListFailure.Validation, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, recordId,
                new UpdateRecordRequest(1, 1, [new FieldChange(field.Id, JsonValue.Create("bad\0value"))])))).Failure);

        Assert.Equal(ListFailure.Forbidden, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.GetListsAsync(new ListActor("bad\0actor"), workspace))).Failure);
        Assert.Equal(ListFailure.Validation, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.SetMembershipAsync(database.AdminActor, workspace, "bad\0member", true, false, false))).Failure);
        Assert.Equal(ListFailure.Validation, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id, "bad\0grant", ListAccess.Read))).Failure);
        Assert.Equal(ListFailure.Validation, (await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.SaveViewAsync(database.AdminActor, workspace, list.Id,
                new SaveViewRequest(Guid.NewGuid(), "bad\0view", new ListQuery(), [field.Id])))).Failure);
        await Assert.ThrowsAsync<ArgumentException>(() => PostgreSqlListSetup.RegisterWorkspaceAsync(
            database.AdminSource!, Guid.NewGuid(), "bad\0workspace", database.AdminActor.Subject));
        await Assert.ThrowsAsync<ArgumentException>(() => PostgreSqlListSetup.RegisterWorkspaceAsync(
            database.AdminSource!, Guid.NewGuid(), "valid", "bad\0administrator"));
    }

    [PostgreSqlFact]
    public async Task ViewAccountingUsesNormalizedJsonbNumbers()
    {
        var workspace = await database.NewWorkspaceAsync("view-numeric-accounting");
        var field = new ListField(Guid.NewGuid(), "Amount", ListFieldType.Decimal);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Numeric views", [field]));
        var exponentFilters = Enumerable.Range(0, 12)
            .Select(_ => new RecordFilter(field.Id, FilterOperator.Equal, JsonNode.Parse("1e27"))).ToArray();
        var expandedFilters = Enumerable.Range(0, 12)
            .Select(_ => new RecordFilter(field.Id, FilterOperator.Equal,
                JsonNode.Parse("1000000000000000000000000000"))).ToArray();
        var viewId = Guid.NewGuid();
        await database.Engine.SaveViewAsync(database.AdminActor, workspace, list.Id,
            new SaveViewRequest(viewId, "Exponent", new ListQuery(exponentFilters), [field.Id]));
        var afterExponent = await database.GetWorkspaceBytesAsync(workspace);
        await database.Engine.SaveViewAsync(database.AdminActor, workspace, list.Id,
            new SaveViewRequest(viewId, "Exponent", new ListQuery(expandedFilters), [field.Id]));
        var afterExpanded = await database.GetWorkspaceBytesAsync(workspace);
        var auditBytes = AuditBytes(database.AdminActor.Subject, "view.save", 1);
        Assert.Equal(auditBytes, afterExpanded - afterExponent);

        await database.Engine.SaveViewAsync(database.AdminActor, workspace, list.Id,
            new SaveViewRequest(viewId, "Exponent", new ListQuery(exponentFilters), [field.Id]));
        var afterSecondExponent = await database.GetWorkspaceBytesAsync(workspace);
        Assert.Equal(auditBytes, afterSecondExponent - afterExpanded);
    }

    [PostgreSqlFact]
    public async Task RecordAccountingUsesNormalizedJsonbNumbersAcrossUpdateAndDelete()
    {
        var workspace = await database.NewWorkspaceAsync("record-numeric-accounting");
        var field = new ListField(Guid.NewGuid(), "Amount", ListFieldType.Decimal);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Numeric records", [field]));
        var before = await database.GetWorkspaceBytesAsync(workspace);
        var recordId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new CreateRecordRequest(recordId, 1, NumberValues(field, "1e27")));
        var afterCreate = await database.GetWorkspaceBytesAsync(workspace);
        Assert.True(afterCreate > before);

        await database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, recordId,
            new UpdateRecordRequest(1, 1, [new FieldChange(field.Id,
                JsonNode.Parse("1000000000000000000000000000"))]));
        var afterEquivalentUpdate = await database.GetWorkspaceBytesAsync(workspace);
        var updateAuditBytes = AuditBytes(database.AdminActor.Subject, "record.update", 1);
        Assert.Equal(updateAuditBytes, afterEquivalentUpdate - afterCreate);

        var smaller = NumberValues(field, "1e26");
        var previousJsonBytes = await database.GetCanonicalJsonBytesAsync(NumberValues(field,
            "1000000000000000000000000000").ToJsonString());
        var smallerJsonBytes = await database.GetCanonicalJsonBytesAsync(smaller.ToJsonString());
        await database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, recordId,
            new UpdateRecordRequest(2, 1, [new FieldChange(field.Id, JsonNode.Parse("1e26"))]));
        var afterSmallerUpdate = await database.GetWorkspaceBytesAsync(workspace);
        Assert.Equal(updateAuditBytes + smallerJsonBytes - previousJsonBytes,
            afterSmallerUpdate - afterEquivalentUpdate);

        await database.Engine.DeleteRecordAsync(database.AdminActor, workspace, list.Id, recordId, 3);
        var afterDelete = await database.GetWorkspaceBytesAsync(workspace);
        var deleteAuditBytes = AuditBytes(database.AdminActor.Subject, "record.delete", 0);
        Assert.Equal(deleteAuditBytes - smallerJsonBytes, afterDelete - afterSmallerUpdate);
        Assert.True(afterDelete > before);
    }

    private static long AuditBytes(string actor, string action, int fieldCount) =>
        96 + System.Text.Encoding.UTF8.GetByteCount(actor) + System.Text.Encoding.UTF8.GetByteCount(action) + fieldCount * 16L;

    private static JsonObject NumberValues(ListField field, string number) =>
        new() { [field.Id.ToString("D")] = JsonNode.Parse(number) };

    private static async Task SetContextAndLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, bool exclusive)
    {
        await using (var context = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction))
        {
            context.Parameters.AddWithValue(workspaceId.ToString("D"));
            await context.ExecuteNonQueryAsync();
        }
        await using var authLock = new NpgsqlCommand(exclusive
            ? "SELECT pg_advisory_xact_lock(hashtextextended($1,391813))"
            : "SELECT pg_advisory_xact_lock_shared(hashtextextended($1,391813))", connection, transaction);
        authLock.Parameters.AddWithValue(workspaceId.ToString("D"));
        await authLock.ExecuteNonQueryAsync();
    }

    private static async Task<ListException?> CaptureAsync(Func<Task> operation)
    {
        try { await operation(); return null; }
        catch (ListException ex) { return ex; }
    }

    private static ListField TextField() => new(Guid.NewGuid(), "Name", ListFieldType.Text, MaxLength: 200);
    private static JsonObject Values(ListField field, string value) => new() { [field.Id.ToString("D")] = value };
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        var configured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CELLBRIDGE_LISTS_TEST_CONNECTION_STRING")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__lists"));
        var required = string.Equals(Environment.GetEnvironmentVariable("CELLBRIDGE_LISTS_REQUIRE_DATABASE"), "true", StringComparison.OrdinalIgnoreCase);
        if (!configured && !required) Skip = "A PostgreSQL list test connection is not configured.";
    }
}

public sealed class ListDatabaseFixture : IAsyncLifetime
{
    private string? _runtimeRole;
    public bool Available { get; private set; }
    public NpgsqlDataSource? AdminSource { get; private set; }
    public NpgsqlDataSource? RuntimeSource { get; private set; }
    public PostgreSqlListEngine Engine { get; private set; } = null!;
    public ListActor AdminActor { get; } = new("test:administrator");

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("CELLBRIDGE_LISTS_TEST_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__lists");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CELLBRIDGE_LISTS_REQUIRE_DATABASE"), "true", StringComparison.OrdinalIgnoreCase))
                Assert.Fail("CELLBRIDGE_LISTS_TEST_CONNECTION_STRING or ConnectionStrings__lists is required.");
            return;
        }
        AdminSource = NpgsqlDataSource.Create(connectionString);
        _runtimeRole = "cellbridge_lists_test_" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await using (var connection = await AdminSource.OpenConnectionAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{_runtimeRole}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT", connection))
            await create.ExecuteNonQueryAsync();
        await PostgreSqlListSetup.InitializeAsync(AdminSource, _runtimeRole);
        var runtimeConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = _runtimeRole,
            Password = password,
            ApplicationName = "CellBridge.Lists.PostgreSql.Tests"
        };
        RuntimeSource = NpgsqlDataSource.Create(runtimeConnection.ConnectionString);
        Engine = new PostgreSqlListEngine(RuntimeSource);
        await Engine.CheckHealthAsync();
        Available = true;
    }

    public async Task<Guid> NewWorkspaceAsync(string name)
    {
        var id = Guid.NewGuid();
        await PostgreSqlListSetup.RegisterWorkspaceAsync(AdminSource!, id, name, AdminActor.Subject);
        return id;
    }

    public async Task SetWorkspaceContextAsync(Guid workspaceId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> operation)
    {
        await using var connection = await RuntimeSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction))
        {
            context.Parameters.AddWithValue(workspaceId.ToString("D"));
            await context.ExecuteNonQueryAsync();
        }
        await operation(connection, transaction);
        await transaction.CommitAsync();
    }

    public async Task<long> GetWorkspaceBytesAsync(Guid workspaceId)
    {
        long result = 0;
        await SetWorkspaceContextAsync(workspaceId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT stored_bytes FROM cellbridge_lists.workspaces WHERE workspace_id=$1", connection, transaction);
            command.Parameters.AddWithValue(workspaceId);
            result = (long)(await command.ExecuteScalarAsync())!;
        });
        return result;
    }

    public async Task<long> GetCanonicalJsonBytesAsync(string json)
    {
        await using var command = AdminSource!.CreateCommand("SELECT octet_length(($1::jsonb)::text)::bigint");
        command.Parameters.AddWithValue(json);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task WaitForAdvisoryWaitAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = AdminSource!.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory'");
            if ((long)(await command.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(20);
        }
        Assert.Fail("The expected advisory-lock waiter did not appear.");
    }

    public async Task DisposeAsync()
    {
        if (RuntimeSource is not null) await RuntimeSource.DisposeAsync();
        if (AdminSource is not null && _runtimeRole is not null)
        {
            await using var connection = await AdminSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{_runtimeRole}\"; DROP ROLE \"{_runtimeRole}\"", connection);
            await cleanup.ExecuteNonQueryAsync();
            await AdminSource.DisposeAsync();
        }
    }
}
