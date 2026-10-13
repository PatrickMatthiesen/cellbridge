using System.Text.Json.Nodes;
using CellBridge.DocumentLibrary;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class ListSeedRecoveryTests
{
    private static readonly ListActor Owner = new(DocumentLibraryService.OwnerSubject);
    private static readonly ListActor Editor = new(DocumentLibraryService.EditorSubject);
    private static readonly ListActor Reader = new(DocumentLibraryService.ReaderSubject);
    private static readonly Guid Workspace = DocumentLibraryListIds.WorkspaceId;
    private static readonly Guid Packages = DocumentLibraryListIds.WorkPackagesListId;
    private static readonly Guid Tasks = DocumentLibraryListIds.TasksListId;
    private static readonly Guid PackageRecord = DocumentLibraryListIds.WorkPackageRecordId;
    private static readonly Guid TaskRecord = DocumentLibraryListIds.TaskRecordId;

    [PostgreSqlFact]
    public async Task WorkspaceOnlyInterruptionResumesAndMetadataIsOperatorOnly()
    {
        await using var database = await SeedDatabase.CreateAsync();
        await database.RegisterWorkspaceAsync();

        await database.SeedAsync();

        Assert.Equal(3, (await database.Engine.GetListsAsync(Owner, Workspace)).Count);
        Assert.Equal(3, (await database.Engine.GetListsAsync(Editor, Workspace)).Count);
        Assert.Equal(3, (await database.Engine.GetListsAsync(Reader, Workspace)).Count);
        Assert.Equal(3, (await database.Engine.GetListsAsync(Owner, DocumentLibraryListIds.OtherWorkspaceId)).Count);
        Assert.Equal(1, (await database.Engine.GetRecordAsync(Owner, Workspace, Packages, PackageRecord)).Number);
        Assert.Equal(1, (await database.Engine.GetRecordAsync(Owner, Workspace, Tasks, TaskRecord)).Number);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM public.cellbridge_document_library_list_seed WHERE workspace_id=$1"));
        var denied = await Assert.ThrowsAsync<ListException>(() => database.Engine.GetListsAsync(Reader, DocumentLibraryListIds.OtherWorkspaceId));
        Assert.Equal(ListFailure.Forbidden, denied.Failure);
        await using var markerRead = database.Runtime.CreateCommand("SELECT * FROM public.cellbridge_document_library_list_seed");
        var sqlDenied = await Assert.ThrowsAsync<PostgresException>(() => markerRead.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, sqlDenied.SqlState);
    }

    [PostgreSqlFact]
    public async Task PartialListsGrantsAndExpiredSeedReceiptResumeWithoutChangingExistingRecord()
    {
        await using var database = await SeedDatabase.CreateAsync();
        await database.RegisterWorkspaceAsync();
        await database.Engine.SetMembershipAsync(Owner, Workspace, Editor.Subject, true, false, false);
        await database.Engine.SetMembershipAsync(Owner, Workspace, Reader.Subject, true, false, false);
        await database.CreatePackagesAsync();
        var tasks = await database.CreateTasksAsync();
        await database.Engine.SetListGrantAsync(Owner, Workspace, Tasks, Editor.Subject, ListAccess.Read | ListAccess.Write);
        await database.Engine.SetListGrantAsync(Owner, Workspace, Tasks, Reader.Subject, ListAccess.Read);
        var added = new ListField(Guid.NewGuid(), "Operator note", ListFieldType.Text);
        await database.Engine.PublishSchemaAsync(Owner, Workspace, Tasks,
            new(tasks.SchemaVersion, tasks.Name, [.. tasks.Fields, added]));
        await database.CreatePackageRecordAsync("Keep the existing operator draft");
        await database.ExpireReceiptsAsync();

        await database.SeedAsync();

        var package = await database.Engine.GetRecordAsync(Owner, Workspace, Packages, PackageRecord);
        Assert.Equal("Keep the existing operator draft", package.Values[DocumentLibraryListIds.WorkPackageTitleFieldId.ToString("D")]!.GetValue<string>());
        Assert.Equal(1, package.Revision);
        Assert.Equal(1, package.Number);
        var task = await database.Engine.GetRecordAsync(Owner, Workspace, Tasks, TaskRecord);
        Assert.Equal(2, task.SchemaVersion);
        Assert.Equal(PackageRecord.ToString("D"), task.Values[DocumentLibraryListIds.TaskWorkPackageFieldId.ToString("D")]!.GetValue<string>());
        Assert.Equal(3, (await database.Engine.GetListsAsync(Editor, Workspace)).Count);
        Assert.Equal(3, (await database.Engine.GetListsAsync(Reader, Workspace)).Count);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM cellbridge_lists.record_tombstones WHERE workspace_id=$1 AND list_id=$2", Packages));
    }

    [PostgreSqlFact]
    public async Task PartialSeedKeepsDeletedPackageIdentityAndOmitsOptionalReference()
    {
        await using var database = await SeedDatabase.CreateAsync();
        await database.RegisterWorkspaceAsync();
        await database.CreatePackagesAsync();
        await database.CreatePackageRecordAsync("Deleted before setup finished");
        await database.Engine.DeleteRecordAsync(Owner, Workspace, Packages, PackageRecord, 1);
        await database.ExpireReceiptsAsync();

        await database.SeedAsync();

        var missing = await Assert.ThrowsAsync<ListException>(() => database.Engine.GetRecordAsync(Owner, Workspace, Packages, PackageRecord));
        Assert.Equal(ListFailure.NotFound, missing.Failure);
        var task = await database.Engine.GetRecordAsync(Owner, Workspace, Tasks, TaskRecord);
        Assert.False(task.Values.ContainsKey(DocumentLibraryListIds.TaskWorkPackageFieldId.ToString("D")));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM cellbridge_lists.record_tombstones WHERE workspace_id=$1 AND list_id=$2", Packages));
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT next_number FROM cellbridge_lists.lists WHERE workspace_id=$1 AND list_id=$2", Packages));
    }

    [PostgreSqlFact]
    public async Task CompletedRerunPreservesEditsDeletedRecordsAndRevokedOwnerMembership()
    {
        await using var database = await SeedDatabase.CreateAsync();
        await database.SeedAsync();
        await DeleteTaskAndEditPackageAsync(database);
        await database.Engine.SetMembershipAsync(Owner, Workspace, Reader.Subject, false, false, false);
        await database.Engine.SetMembershipAsync(Owner, Workspace, Owner.Subject, false, false, false);
        var auditCount = await database.ScalarAsync<long>("SELECT count(*) FROM cellbridge_lists.audit_events WHERE workspace_id=$1");

        await database.SeedAsync();

        await AssertUserChangesPreservedAsync(database);
        Assert.Equal(auditCount, await database.ScalarAsync<long>("SELECT count(*) FROM cellbridge_lists.audit_events WHERE workspace_id=$1"));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM cellbridge_lists.memberships WHERE workspace_id=$1 AND subject=$2", Owner.Subject));
        var denied = await Assert.ThrowsAsync<ListException>(() => database.Engine.GetListsAsync(Reader, Workspace));
        Assert.Equal(ListFailure.Forbidden, denied.Failure);
    }

    [PostgreSqlFact]
    public async Task CompletedLegacyWorkspaceGetsMarkerWithoutRestoringAuthorityOrDeletedSeeds()
    {
        await using var database = await SeedDatabase.CreateAsync();
        await database.SeedAsync();
        await database.SqlAsync("DELETE FROM public.cellbridge_document_library_list_seed WHERE workspace_id=$1");
        await DeleteTaskAndEditPackageAsync(database);
        await database.ExpireReceiptsAsync();
        await database.Engine.SetListGrantAsync(Owner, Workspace, Tasks, Reader.Subject, ListAccess.None);
        await database.Engine.SetMembershipAsync(Owner, Workspace, Owner.Subject, false, false, false);
        var auditCount = await database.ScalarAsync<long>("SELECT count(*) FROM cellbridge_lists.audit_events WHERE workspace_id=$1");

        await database.SeedAsync();

        await AssertUserChangesPreservedAsync(database);
        Assert.Equal(auditCount, await database.ScalarAsync<long>("SELECT count(*) FROM cellbridge_lists.audit_events WHERE workspace_id=$1"));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM public.cellbridge_document_library_list_seed WHERE workspace_id=$1"));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM cellbridge_lists.memberships WHERE workspace_id=$1 AND subject=$2", Owner.Subject));
        var denied = await Assert.ThrowsAsync<ListException>(() => database.Engine.GetListAsync(Reader, Workspace, Tasks));
        Assert.Equal(ListFailure.NotFound, denied.Failure);
    }

    [PostgreSqlFact]
    public async Task ConcurrentSetupInvocationsCreateEachListAndSeedOnlyOnce()
    {
        await using var database = await SeedDatabase.CreateAsync();

        await Task.WhenAll(database.SeedAsync(), database.SeedAsync());

        foreach (var workspace in new[] { Workspace, DocumentLibraryListIds.OtherWorkspaceId })
        {
            Assert.Equal(3, (await database.Engine.GetListsAsync(Owner, workspace)).Count);
            Assert.Single((await database.Engine.QueryAsync(Owner, workspace, Packages, new())).Records);
            Assert.Single((await database.Engine.QueryAsync(Owner, workspace, Tasks, new())).Records);
            var audit = await database.Engine.GetAuditAsync(Owner, workspace);
            Assert.Equal(3, audit.Count(e => e.Action == "list.create"));
            Assert.Equal(2, audit.Count(e => e.Action == "record.create"));
        }
    }

    [PostgreSqlFact]
    public async Task SetupRejectsMetadataPrivilegeThroughNoInheritParentRole()
    {
        await using var database = await SeedDatabase.CreateAsync();
        await database.SeedAsync();
        await database.GrantMetadataParentAsync();
        await using (var directAccess = database.Runtime.CreateCommand(
            "SELECT has_table_privilege(current_user,'public.cellbridge_document_library_list_seed','INSERT')"))
            Assert.False((bool)(await directAccess.ExecuteScalarAsync())!);

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => database.SeedAsync());

        Assert.Contains("no access to sample setup completion metadata", rejected.Message);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM public.cellbridge_document_library_list_seed WHERE workspace_id=$1"));
    }

    private static async Task DeleteTaskAndEditPackageAsync(SeedDatabase database)
    {
        var task = await database.Engine.GetRecordAsync(Owner, Workspace, Tasks, TaskRecord);
        await database.Engine.DeleteRecordAsync(Owner, Workspace, Tasks, TaskRecord, task.Revision);
        var package = await database.Engine.GetRecordAsync(Owner, Workspace, Packages, PackageRecord);
        var definition = await database.Engine.GetListAsync(Owner, Workspace, Packages);
        await database.Engine.UpdateRecordAsync(Owner, Workspace, Packages, PackageRecord,
            new(package.Revision, definition.SchemaVersion,
                [new(DocumentLibraryListIds.WorkPackageTitleFieldId, JsonValue.Create("Customer-edited title"))]));
    }

    private static async Task AssertUserChangesPreservedAsync(SeedDatabase database)
    {
        var package = await database.Engine.GetRecordAsync(Editor, Workspace, Packages, PackageRecord);
        Assert.Equal("Customer-edited title", package.Values[DocumentLibraryListIds.WorkPackageTitleFieldId.ToString("D")]!.GetValue<string>());
        Assert.Equal(2, package.Revision);
        var deleted = await Assert.ThrowsAsync<ListException>(() => database.Engine.GetRecordAsync(Editor, Workspace, Tasks, TaskRecord));
        Assert.Equal(ListFailure.NotFound, deleted.Failure);
    }

    private sealed class SeedDatabase : IAsyncDisposable
    {
        private readonly IsolatedPostgreSqlDatabase database;
        private readonly string role;
        private readonly List<string> parentRoles = [];
        private SeedDatabase(IsolatedPostgreSqlDatabase database, NpgsqlDataSource admin, NpgsqlDataSource runtime, string role)
        {
            this.database = database; this.role = role;
            Admin = admin; Runtime = runtime; Engine = new(runtime);
        }
        public NpgsqlDataSource Admin { get; }
        public NpgsqlDataSource Runtime { get; }
        public PostgreSqlListEngine Engine { get; }

        public static async Task<SeedDatabase> CreateAsync()
        {
            var database = await IsolatedPostgreSqlDatabase.CreateAsync();
            var admin = database.CreateSource();
            var role = "cellbridge_seed_test_" + Guid.NewGuid().ToString("N");
            var password = Guid.NewGuid().ToString("N");
            try
            {
                await using var create = admin.CreateCommand($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT");
                await create.ExecuteNonQueryAsync();
                await PostgreSqlListSetup.InitializeAsync(admin, role);
                var connection = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Username = role, Password = password };
                var runtime = NpgsqlDataSource.Create(connection.ConnectionString);
                return new(database, admin, runtime, role);
            }
            catch
            {
                await admin.DisposeAsync();
                await database.DisposeAsync();
                await DropRoleAsync(role);
                throw;
            }
        }

        public Task SeedAsync() => DocumentLibraryListSeed.InitializeAsync(Admin, Runtime);
        public async Task GrantMetadataParentAsync()
        {
            var parent = "cellbridge_seed_parent_" + Guid.NewGuid().ToString("N");
            await using var command = Admin.CreateCommand($"""
                CREATE ROLE "{parent}" NOLOGIN NOSUPERUSER NOBYPASSRLS;
                GRANT INSERT ON public.cellbridge_document_library_list_seed TO "{parent}";
                GRANT "{parent}" TO "{role}";
                """);
            await command.ExecuteNonQueryAsync();
            parentRoles.Add(parent);
        }
        public Task RegisterWorkspaceAsync() => PostgreSqlListSetup.RegisterWorkspaceAsync(Admin, Workspace, "Acme", Owner.Subject);
        public Task ExpireReceiptsAsync() => SqlAsync("UPDATE cellbridge_lists.create_receipts SET expires_utc=clock_timestamp()-interval '1 day' WHERE workspace_id=$1");
        public Task<ListDefinition> CreatePackagesAsync() => Engine.CreateListAsync(Owner, Workspace,
            new(Packages, "Work packages", [new(DocumentLibraryListIds.WorkPackageTitleFieldId, "Title", ListFieldType.Text, Required: true)], NumberPrefix: "WP-"));
        public async Task CreatePackageRecordAsync(string title)
        {
            var definition = await Engine.GetListAsync(Owner, Workspace, Packages);
            await Engine.CreateRecordAsync(Owner, Workspace, Packages,
                new(PackageRecord, definition.SchemaVersion, new JsonObject { [DocumentLibraryListIds.WorkPackageTitleFieldId.ToString("D")] = title }));
        }
        public Task<ListDefinition> CreateTasksAsync() => Engine.CreateListAsync(Owner, Workspace,
            new(Tasks, "Tasks",
                [new(DocumentLibraryListIds.TaskTitleFieldId, "Title", ListFieldType.Text, Required: true),
                 new(DocumentLibraryListIds.TaskStatusFieldId, "Status", ListFieldType.Choice, Required: true,
                    Choices: [new("todo", "To do"), new("doing", "In progress"), new("done", "Done")]),
                 new(DocumentLibraryListIds.TaskWorkPackageFieldId, "Work package", ListFieldType.Reference,
                    ReferenceListId: Packages, ReferenceDisplayFieldId: DocumentLibraryListIds.WorkPackageTitleFieldId),
                 new(DocumentLibraryListIds.TaskExternalIdFieldId, "External ID", ListFieldType.Text)], NumberPrefix: "TASK-"));

        public async Task<T> ScalarAsync<T>(string sql, params object[] values)
        {
            await using var connection = await Admin.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SetWorkspaceAsync(connection, transaction);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(Workspace);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected a test value."));
        }
        public async Task SqlAsync(string sql, params object[] values)
        {
            await using var connection = await Admin.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SetWorkspaceAsync(connection, transaction);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(Workspace);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        private static async Task SetWorkspaceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            await using var command = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction);
            command.Parameters.AddWithValue(Workspace.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Admin.DisposeAsync();
            await database.DisposeAsync();
            await DropRoleAsync(role);
            foreach (var parent in parentRoles) await DropRoleAsync(parent);
        }
        private static async Task DropRoleAsync(string role)
        {
            await using var admin = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
            await using var drop = admin.CreateCommand($"DROP ROLE IF EXISTS \"{role}\"");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
