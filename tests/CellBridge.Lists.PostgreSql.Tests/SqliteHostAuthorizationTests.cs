using System.Text.Json.Nodes;
using CellBridge.Examples.HostAuthorizedLists;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class SqliteHostAuthorizationTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task SQLiteAuthorityRollbackPreservesPermissionsAndAccountDisableRevokesAccess()
    {
        var sqlitePath = Path.Combine(Path.GetTempPath(), $"cellbridge-list-authority-test-{Guid.NewGuid():N}.db");
        try
        {
            const string domain = "test:sqlite-host-authority";
            var workspace = Guid.NewGuid();
            var actor = new ListActor("host:sqlite:" + Guid.NewGuid().ToString("N"));
            await PostgreSqlListSetup.RegisterHostWorkspaceAsync(
                database.AdminSource!, workspace, "SQLite host authority", domain);

            await using var authority = new SqliteListAuthority(sqlitePath, domain);
            await authority.InitializeAsync();
            var provider = new SingleProcessListAuthorizationProvider(domain, authority.LoadSnapshotAsync);
            var engine = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits(), provider);
            var listId = Guid.NewGuid();

            await engine.CoordinateGlobalAuthorizationChangeAsync(ct =>
                authority.InTransactionAsync(async (change, token) =>
                {
                    await change.SetAccountEnabledAsync(actor.Subject, true, token);
                    await change.SetWorkspacePermissionAsync(actor.Subject, workspace,
                        ListWorkspaceAccess.UseLists | ListWorkspaceAccess.CreateLists,
                        ListAccess.None,
                        new Dictionary<Guid, ListAccess>
                        {
                            [listId] = ListAccess.Read | ListAccess.Write | ListAccess.Design
                        }, token);
                    return true;
                }, ct));

            var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
            var list = await engine.CreateListAsync(actor, workspace,
                new CreateListRequest(listId, "Authorized", [field]));
            var record = await engine.CreateRecordAsync(actor, workspace, list.Id,
                new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "before rollback")));
            Assert.Equal((0L, 0L), await AclCountsAsync(workspace));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.CoordinateAuthorizationChangeAsync(workspace, ct =>
                    authority.InTransactionAsync<bool>(async (change, token) =>
                    {
                        await change.SetWorkspacePermissionAsync(actor.Subject, workspace,
                            ListWorkspaceAccess.UseLists, ListAccess.None, ct: token);
                        throw new InvalidOperationException("Force the SQLite permission transaction to roll back.");
                    }, ct)));

            Assert.Equal(record.Id,
                (await engine.GetRecordAsync(actor, workspace, list.Id, record.Id)).Id);
            await engine.CreateRecordAsync(actor, workspace, list.Id,
                new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "after rollback")));

            await engine.CoordinateGlobalAuthorizationChangeAsync(ct =>
                authority.InTransactionAsync(async (change, token) =>
                {
                    await change.SetAccountEnabledAsync(actor.Subject, false, token);
                    return true;
                }, ct));

            await AssertFailureAsync(ListFailure.Forbidden,
                () => engine.GetRecordAsync(actor, workspace, list.Id, record.Id));
            await AssertFailureAsync(ListFailure.Forbidden,
                () => engine.CreateRecordAsync(actor, workspace, list.Id,
                    new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "after disable"))));
        }
        finally
        {
            if (File.Exists(sqlitePath)) File.Delete(sqlitePath);
        }
    }

    private static JsonObject Values(ListField field, string value) =>
        new() { [field.Id.ToString("D")] = value };

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

    private static async Task AssertFailureAsync(ListFailure expected, Func<Task> action)
    {
        var failure = await Assert.ThrowsAsync<ListException>(action);
        Assert.Equal(expected, failure.Failure);
    }
}
