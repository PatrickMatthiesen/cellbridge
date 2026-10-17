using System.Text;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class AuthorityReviewRegressionTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task RevocationReleasesMembershipAndAllGrantBytesButRetainsAuditUsage()
    {
        var workspace = await database.NewWorkspaceAsync("authority-quota");
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var first = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "First", [field]));
        var second = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Second", [field]));
        var subject = "test:member:æ:" + Guid.NewGuid().ToString("N");
        var subjectBytes = Encoding.UTF8.GetByteCount(subject);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = await database.GetWorkspaceBytesAsync(workspace);
            await database.Engine.SetMembershipAsync(database.AdminActor, workspace, subject, true, false, false);
            await database.Engine.SetListGrantAsync(database.AdminActor, workspace, first.Id, subject, ListAccess.Read);
            await database.Engine.SetListGrantAsync(database.AdminActor, workspace, second.Id, subject, ListAccess.Read);
            var charged = await database.GetWorkspaceBytesAsync(workspace);

            await database.Engine.SetListGrantAsync(database.AdminActor, workspace, first.Id, subject, ListAccess.None);
            var afterGrant = await database.GetWorkspaceBytesAsync(workspace);
            Assert.Equal(charged - 80 - subjectBytes, afterGrant);
            await database.Engine.SetMembershipAsync(database.AdminActor, workspace, subject, false, false, false);
            var afterMember = await database.GetWorkspaceBytesAsync(workspace);
            Assert.Equal(afterGrant - 128 - subjectBytes - 80 - subjectBytes, afterMember);

            var retainedAuditBytes = AuditBytes("membership.set", subject) + 2 * AuditBytes("grant.set", subject);
            Assert.Equal(before + retainedAuditBytes, afterMember);
        }
    }

    [PostgreSqlFact]
    public async Task BootstrapMembershipAndCreatorGrantUseTheSameQuotaCharges()
    {
        var workspace = await database.NewWorkspaceAsync("bootstrap-quota");
        var subjectBytes = Encoding.UTF8.GetByteCount(database.AdminActor.Subject);
        Assert.Equal(128 + subjectBytes, await database.GetWorkspaceBytesAsync(workspace));
        await PostgreSqlListSetup.RegisterWorkspaceAsync(database.AdminSource!, workspace, "bootstrap-quota", database.AdminActor.Subject);
        Assert.Equal(128 + subjectBytes, await database.GetWorkspaceBytesAsync(workspace));

        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Owned", [field]));
        var withGrant = await database.GetWorkspaceBytesAsync(workspace);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, list.Id,
            database.AdminActor.Subject, ListAccess.None);
        Assert.Equal(withGrant - 80 - subjectBytes, await database.GetWorkspaceBytesAsync(workspace));
        await database.Engine.SetMembershipAsync(database.AdminActor, workspace, database.AdminActor.Subject, false, false, false);
        Assert.Equal(withGrant - 80 - subjectBytes - 128 - subjectBytes, await database.GetWorkspaceBytesAsync(workspace));
    }

    [PostgreSqlFact]
    public async Task HealthRejectsExcessParentPrivilegesOnEveryRuntimeTable()
    {
        var parent = "lists_privilege_parent_" + Guid.NewGuid().ToString("N");
        var runtime = new NpgsqlConnectionStringBuilder(database.RuntimeSource!.ConnectionString).Username!;
        var quotedParent = Quote(parent);
        var quotedRuntime = Quote(runtime);
        await AdminSqlAsync($"CREATE ROLE {quotedParent} NOLOGIN NOSUPERUSER NOBYPASSRLS");
        await AdminSqlAsync($"GRANT {quotedParent} TO {quotedRuntime}");
        try
        {
            var excess = new[]
            {
                ("schema_version", "UPDATE"), ("templates", "UPDATE"), ("workspaces", "DELETE"),
                ("memberships", "TRUNCATE"), ("lists", "DELETE"), ("list_schemas", "UPDATE"),
                ("list_grants", "TRUNCATE"), ("records", "TRUNCATE"), ("record_tombstones", "UPDATE"),
                ("create_receipts", "UPDATE"), ("saved_views", "TRUNCATE"), ("audit_events", "UPDATE")
            };
            foreach (var (table, privilege) in excess)
            {
                await AdminSqlAsync($"GRANT {privilege} ON cellbridge_lists.{table} TO {quotedParent}");
                try
                {
                    // Setup can revoke direct grants but must still detect a parent's privilege.
                    await PostgreSqlListSetup.InitializeAsync(database.AdminSource!, runtime);
                    var failure = await Assert.ThrowsAsync<ListException>(() =>
                        new PostgreSqlListEngine(database.RuntimeSource).CheckHealthAsync());
                    Assert.Equal(ListFailure.Unavailable, failure.Failure);
                }
                finally
                {
                    await AdminSqlAsync($"REVOKE {privilege} ON cellbridge_lists.{table} FROM {quotedParent}");
                }
            }

            await AdminSqlAsync($"GRANT CREATE ON SCHEMA cellbridge_lists TO {quotedParent}");
            Assert.Equal(ListFailure.Unavailable, (await Assert.ThrowsAsync<ListException>(() =>
                new PostgreSqlListEngine(database.RuntimeSource).CheckHealthAsync())).Failure);
            await AdminSqlAsync($"REVOKE CREATE ON SCHEMA cellbridge_lists FROM {quotedParent}");

            await AdminSqlAsync($"GRANT UPDATE (record_id) ON cellbridge_lists.record_tombstones TO {quotedParent}");
            Assert.Equal(ListFailure.Unavailable, (await Assert.ThrowsAsync<ListException>(() =>
                new PostgreSqlListEngine(database.RuntimeSource).CheckHealthAsync())).Failure);
            await AdminSqlAsync($"REVOKE UPDATE (record_id) ON cellbridge_lists.record_tombstones FROM {quotedParent}");

            await AdminSqlAsync($"GRANT UPDATE ON SEQUENCE cellbridge_lists.audit_events_id_seq TO {quotedParent}");
            Assert.Equal(ListFailure.Unavailable, (await Assert.ThrowsAsync<ListException>(() =>
                new PostgreSqlListEngine(database.RuntimeSource).CheckHealthAsync())).Failure);
        }
        finally
        {
            await AdminSqlAsync($"REVOKE {quotedParent} FROM {quotedRuntime}");
            await AdminSqlAsync($"DROP OWNED BY {quotedParent}");
            await AdminSqlAsync($"DROP ROLE {quotedParent}");
        }
        await new PostgreSqlListEngine(database.RuntimeSource).CheckHealthAsync();
    }

    private int AuditBytes(string action, string subject) => 96 + Encoding.UTF8.GetByteCount(database.AdminActor.Subject)
        + Encoding.UTF8.GetByteCount(action) + Encoding.UTF8.GetByteCount(subject);

    private async Task AdminSqlAsync(string sql)
    {
        await using var command = database.AdminSource!.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
}
