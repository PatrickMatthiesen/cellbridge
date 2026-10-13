using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.DocumentLibrary;

// Operator provisioning for local example identities. Never run from an HTTP handler.
public static class DocumentLibraryListSeed
{
    private const string Owner = "local:owner";
    private const string Editor = "local:editor";
    private const string Reader = "local:reader";

    public static async Task InitializeAsync(NpgsqlDataSource admin, NpgsqlDataSource runtime, CancellationToken ct = default)
    {
        await using var connection = await admin.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Serialize sample setup invocations, including independent processes. List
        // operations still use their own authorized provider transactions.
        await using (var setupLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1128418375,1279873876)", connection, transaction))
            await setupLock.ExecuteNonQueryAsync(ct);
        await using (var metadata = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS public.cellbridge_document_library_list_seed (
              workspace_id uuid PRIMARY KEY,
              completed_utc timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            REVOKE ALL ON public.cellbridge_document_library_list_seed FROM PUBLIC;
            """, connection, transaction)) await metadata.ExecuteNonQueryAsync(ct);
        await using (var user = runtime.CreateCommand("SELECT current_user"))
        {
            var runtimeRole = (string)(await user.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("A runtime role is required."));
            var quotedRole = '"' + runtimeRole.Replace("\"", "\"\"") + '"';
            await using var revoke = new NpgsqlCommand($"REVOKE ALL ON public.cellbridge_document_library_list_seed FROM {quotedRole}", connection, transaction);
            await revoke.ExecuteNonQueryAsync(ct);
            await using var privileges = new NpgsqlCommand("""
                SELECT EXISTS (
                  SELECT 1 FROM pg_roles parent WHERE pg_has_role($1,parent.oid,'MEMBER') AND
                    (has_table_privilege(parent.oid,'public.cellbridge_document_library_list_seed',
                      'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    OR has_any_column_privilege(parent.oid,'public.cellbridge_document_library_list_seed',
                      'SELECT,INSERT,UPDATE,REFERENCES'))
                )
                """, connection, transaction);
            privileges.Parameters.AddWithValue(runtimeRole);
            if ((bool)(await privileges.ExecuteScalarAsync(ct) ?? true))
                throw new InvalidOperationException("The list runtime role must have no access to sample setup completion metadata.");
        }

        var engine = new PostgreSqlListEngine(runtime);
        var actor = new ListActor(Owner);
        var ids = new[] { DocumentLibraryListIds.WorkspaceId, DocumentLibraryListIds.OtherWorkspaceId };
        foreach (var workspace in ids)
        {
            await using (var context = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction))
            { context.Parameters.AddWithValue(workspace.ToString("D")); await context.ExecuteNonQueryAsync(ct); }
            if (await CompletedAsync(connection, transaction, workspace, ct)) continue;
            // The original seed's final mutation created the task, after every list,
            // membership and grant. Durable identities survive edits, deletes and
            // receipt expiry, so a completed legacy seed needs no authority changes.
            if (await LegacyCompletedAsync(connection, transaction, workspace, ct))
            {
                await MarkCompletedAsync(connection, transaction, workspace, ct);
                continue;
            }
            if (!await ExistsAsync(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM cellbridge_lists.workspaces WHERE workspace_id=$1)", workspace, ct))
                await PostgreSqlListSetup.RegisterWorkspaceAsync(admin, workspace,
                    workspace == ids[0] ? "Acme" : "Contoso", Owner, ct);
            if (workspace == ids[0])
            {
                await engine.SetMembershipAsync(actor, workspace, Editor, true, false, false, ct);
                await engine.SetMembershipAsync(actor, workspace, Reader, true, false, false, ct);
            }
            var packages = await CreateTemplateListAsync(admin, engine, actor, workspace,
                DocumentLibraryListIds.WorkPackagesListId, "Work packages",
                [new(DocumentLibraryListIds.WorkPackageTitleFieldId, "Title", ListFieldType.Text, Required: true)], "WP-", ct);
            var tasks = await CreateTemplateListAsync(admin, engine, actor, workspace,
                DocumentLibraryListIds.TasksListId, "Tasks",
                    [new(DocumentLibraryListIds.TaskTitleFieldId, "Title", ListFieldType.Text, Required: true),
                     new(DocumentLibraryListIds.TaskStatusFieldId, "Status", ListFieldType.Choice, Required: true,
                         Choices: [new("todo", "To do"), new("doing", "In progress"), new("done", "Done")]),
                     new(DocumentLibraryListIds.TaskWorkPackageFieldId, "Work package", ListFieldType.Reference,
                         ReferenceListId: packages.Id, ReferenceDisplayFieldId: DocumentLibraryListIds.WorkPackageTitleFieldId),
                     new(DocumentLibraryListIds.TaskExternalIdFieldId, "External ID", ListFieldType.Text,
                         Display: new(Hidden: true, ReadOnly: true, Tags: ["automation"]))], "TASK-", ct);
            var deliverables = await CreateTemplateListAsync(admin, engine, actor, workspace,
                DocumentLibraryListIds.DeliverablesListId, "Deliverables",
                [new(DocumentLibraryListIds.DeliverableTitleFieldId, "Title", ListFieldType.Text, Required: true)], "D-", ct);
            if (workspace == ids[0])
                foreach (var list in new[] { packages, tasks, deliverables })
                {
                    await engine.SetListGrantAsync(actor, workspace, list.Id, Editor, ListAccess.Read | ListAccess.Write, ct);
                    await engine.SetListGrantAsync(actor, workspace, list.Id, Reader, ListAccess.Read, ct);
                }
            if (!await SeedIdentityUsedAsync(connection, transaction, workspace, packages.Id, DocumentLibraryListIds.WorkPackageRecordId, ct))
                await engine.CreateRecordAsync(actor, workspace, packages.Id,
                    new CreateRecordRequest(DocumentLibraryListIds.WorkPackageRecordId, packages.SchemaVersion,
                        new JsonObject { [DocumentLibraryListIds.WorkPackageTitleFieldId.ToString("D")] = "Customer onboarding" }), ct);
            if (!await SeedIdentityUsedAsync(connection, transaction, workspace, tasks.Id, DocumentLibraryListIds.TaskRecordId, ct))
            {
                var values = new JsonObject
                {
                    [DocumentLibraryListIds.TaskTitleFieldId.ToString("D")] = "Confirm customer requirements",
                    [DocumentLibraryListIds.TaskStatusFieldId.ToString("D")] = "todo",
                    [DocumentLibraryListIds.TaskExternalIdFieldId.ToString("D")] = "CRM-1042"
                };
                // An interrupted operator seed may already have a deleted package.
                // Its identity stays used; omit this optional reference rather than
                // restoring the package or weakening reference validation.
                try
                {
                    await engine.GetRecordAsync(actor, workspace, packages.Id, DocumentLibraryListIds.WorkPackageRecordId, ct);
                    values[DocumentLibraryListIds.TaskWorkPackageFieldId.ToString("D")] = DocumentLibraryListIds.WorkPackageRecordId.ToString("D");
                }
                catch (ListException error) when (error.Failure == ListFailure.NotFound) { }
                await engine.CreateRecordAsync(actor, workspace, tasks.Id,
                    new CreateRecordRequest(DocumentLibraryListIds.TaskRecordId, tasks.SchemaVersion, values), ct);
            }
            await MarkCompletedAsync(connection, transaction, workspace, ct);
        }
        // A crash before this commit leaves no completion claim. Provider writes
        // already committed are inspected and retained on the next setup run.
        await transaction.CommitAsync(ct);
    }

    private static async Task<ListDefinition> CreateTemplateListAsync(NpgsqlDataSource admin, IListEngine engine,
        ListActor actor, Guid workspace, Guid listId, string name, IReadOnlyList<ListField> fields, string prefix, CancellationToken ct)
    {
        try { return await engine.GetListAsync(actor, workspace, listId, ct); }
        catch (ListException error) when (error.Failure == ListFailure.NotFound) { }
        var template = new ListTemplate(listId, name, 1, fields);
        await PostgreSqlListSetup.RegisterTemplateAsync(admin, template, ct);
        return await engine.CreateListAsync(actor, workspace,
            new CreateListRequest(listId, name, [], template.Id, template.Version, prefix), ct);
    }

    private static Task<bool> CompletedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspace, CancellationToken ct) =>
        ExistsAsync(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM public.cellbridge_document_library_list_seed WHERE workspace_id=$1)", workspace, ct);

    private static Task<bool> LegacyCompletedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspace, CancellationToken ct) =>
        ExistsAsync(connection, transaction, """
            SELECT (SELECT count(*)=3 FROM cellbridge_lists.lists
              WHERE workspace_id=$1 AND list_id IN ($2,$3,$4))
              AND EXISTS(SELECT 1 FROM cellbridge_lists.record_tombstones WHERE workspace_id=$1 AND list_id=$2 AND record_id=$5)
              AND EXISTS(SELECT 1 FROM cellbridge_lists.record_tombstones WHERE workspace_id=$1 AND list_id=$3 AND record_id=$6)
            """, workspace, ct, DocumentLibraryListIds.WorkPackagesListId, DocumentLibraryListIds.TasksListId,
            DocumentLibraryListIds.DeliverablesListId, DocumentLibraryListIds.WorkPackageRecordId, DocumentLibraryListIds.TaskRecordId);

    private static Task<bool> SeedIdentityUsedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspace, Guid listId, Guid recordId, CancellationToken ct) => ExistsAsync(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM cellbridge_lists.record_tombstones WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3)",
            workspace, ct, listId, recordId);

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, Guid workspace, CancellationToken ct, params Guid[] ids)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(workspace);
        foreach (var id in ids) command.Parameters.AddWithValue(id);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task MarkCompletedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspace, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO public.cellbridge_document_library_list_seed(workspace_id) VALUES ($1)", connection, transaction);
        command.Parameters.AddWithValue(workspace);
        await command.ExecuteNonQueryAsync(ct);
    }
}
