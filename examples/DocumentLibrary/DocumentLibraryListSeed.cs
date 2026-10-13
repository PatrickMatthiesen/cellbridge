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
        var engine = new PostgreSqlListEngine(runtime);
        var actor = new ListActor(Owner);
        var ids = new[] { DocumentLibraryListIds.WorkspaceId, DocumentLibraryListIds.OtherWorkspaceId };
        foreach (var workspace in ids)
        {
            // Existing workspaces preserve user edits, deleted seed records and revoked access.
            if (await WorkspaceExistsAsync(admin, workspace, ct)) continue;
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
            await engine.CreateRecordAsync(actor, workspace, packages.Id,
                new CreateRecordRequest(DocumentLibraryListIds.WorkPackageRecordId, 1,
                    new JsonObject { [DocumentLibraryListIds.WorkPackageTitleFieldId.ToString()] = "Customer onboarding" }), ct);
            await engine.CreateRecordAsync(actor, workspace, tasks.Id,
                new CreateRecordRequest(DocumentLibraryListIds.TaskRecordId, 1,
                    new JsonObject
                    {
                        [DocumentLibraryListIds.TaskTitleFieldId.ToString()] = "Confirm customer requirements",
                        [DocumentLibraryListIds.TaskStatusFieldId.ToString()] = "todo",
                        [DocumentLibraryListIds.TaskWorkPackageFieldId.ToString()] = DocumentLibraryListIds.WorkPackageRecordId.ToString(),
                        [DocumentLibraryListIds.TaskExternalIdFieldId.ToString()] = "CRM-1042"
                    }), ct);
        }
    }

    private static async Task<ListDefinition> CreateTemplateListAsync(NpgsqlDataSource admin, IListEngine engine,
        ListActor actor, Guid workspace, Guid listId, string name, IReadOnlyList<ListField> fields, string prefix, CancellationToken ct)
    {
        var template = new ListTemplate(listId, name, 1, fields);
        await PostgreSqlListSetup.RegisterTemplateAsync(admin, template, ct);
        return await engine.CreateListAsync(actor, workspace,
            new CreateListRequest(listId, name, [], template.Id, template.Version, prefix), ct);
    }

    private static async Task<bool> WorkspaceExistsAsync(NpgsqlDataSource admin, Guid workspace, CancellationToken ct)
    {
        await using var connection = await admin.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, tx))
        { context.Parameters.AddWithValue(workspace.ToString()); await context.ExecuteNonQueryAsync(ct); }
        await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM cellbridge_lists.workspaces WHERE workspace_id=$1)", connection, tx);
        exists.Parameters.AddWithValue(workspace);
        return (bool)(await exists.ExecuteScalarAsync(ct) ?? false);
    }
}
