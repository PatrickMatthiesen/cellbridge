using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;
using NpgsqlTypes;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class HostWorkspaceSetupTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task HostWorkspaceBindingPreservesOpaqueDomainAndRejectsModeChanges()
    {
        var builtinWorkspace = await database.NewWorkspaceAsync("built-in original");
        var builtin = await ReadWorkspaceAsync(builtinWorkspace);
        Assert.Equal("built-in original", builtin.Name);
        Assert.Null(builtin.AuthorizationDomain);
        Assert.Equal(1, builtin.Memberships);

        var builtinHostFailure = await Assert.ThrowsAsync<ListException>(() =>
            PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, builtinWorkspace,
                "built-in changed", "sharebridge:lists"));
        Assert.Equal(ListFailure.Conflict, builtinHostFailure.Failure);
        builtin = await ReadWorkspaceAsync(builtinWorkspace);
        Assert.Equal("built-in original", builtin.Name);
        Assert.Null(builtin.AuthorizationDomain);
        Assert.Equal(1, builtin.Memberships);

        var hostWorkspace = Guid.NewGuid();
        const string opaqueDomain = "sharebridge:lists";
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, hostWorkspace,
            "host original", opaqueDomain);
        var host = await ReadWorkspaceAsync(hostWorkspace);
        Assert.Equal("host original", host.Name);
        Assert.Equal(opaqueDomain, host.AuthorizationDomain);
        Assert.Equal(0, host.Memberships);
        Assert.Equal(0, host.Grants);

        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, hostWorkspace,
            "host renamed", opaqueDomain);
        host = await ReadWorkspaceAsync(hostWorkspace);
        Assert.Equal("host renamed", host.Name);
        Assert.Equal(opaqueDomain, host.AuthorizationDomain);
        Assert.Equal(0, host.Memberships);

        var differentDomainFailure = await Assert.ThrowsAsync<ListException>(() =>
            PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, hostWorkspace,
                "should not rename", "sharebridge:Lists"));
        Assert.Equal(ListFailure.Conflict, differentDomainFailure.Failure);

        var builtinRegistrationFailure = await Assert.ThrowsAsync<ListException>(() =>
            PostgreSqlListSetup.RegisterWorkspaceAsync(database.AdminSource!, hostWorkspace,
                "should not rename", database.AdminActor.Subject));
        Assert.Equal(ListFailure.Conflict, builtinRegistrationFailure.Failure);
        host = await ReadWorkspaceAsync(hostWorkspace);
        Assert.Equal("host renamed", host.Name);
        Assert.Equal(opaqueDomain, host.AuthorizationDomain);
        Assert.Equal(0, host.Memberships);
        Assert.Equal(0, host.Grants);
    }

    [PostgreSqlFact]
    public async Task RuntimeCannotChangeWorkspaceAuthorizationBinding()
    {
        var hostWorkspace = Guid.NewGuid();
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, hostWorkspace,
            "host immutable", "sharebridge:lists");

        await AssertRebindingRejectedAsync(hostWorkspace, null);
        await AssertRebindingRejectedAsync(hostWorkspace, "sharebridge:other");
        var host = await ReadWorkspaceAsync(hostWorkspace);
        Assert.Equal("sharebridge:lists", host.AuthorizationDomain);

        var builtinWorkspace = await database.NewWorkspaceAsync("built-in immutable");
        await AssertRebindingRejectedAsync(builtinWorkspace, "sharebridge:lists");
        var builtin = await ReadWorkspaceAsync(builtinWorkspace);
        Assert.Null(builtin.AuthorizationDomain);

        await database.SetWorkspaceContextAsync(hostWorkspace, async (connection, transaction) =>
        {
            await using var update = new NpgsqlCommand("UPDATE cellbridge_lists.workspaces SET name=$2,stored_bytes=stored_bytes+0 WHERE workspace_id=$1", connection, transaction);
            update.Parameters.AddWithValue(hostWorkspace);
            update.Parameters.AddWithValue("runtime update succeeded");
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        });
        host = await ReadWorkspaceAsync(hostWorkspace);
        Assert.Equal("runtime update succeeded", host.Name);
        Assert.Equal("sharebridge:lists", host.AuthorizationDomain);
    }

    private async Task<(string Name, string? AuthorizationDomain, long Memberships, long Grants)> ReadWorkspaceAsync(
        Guid workspaceId)
    {
        await using var command = database.AdminSource!.CreateCommand("""
            SELECT w.name,w.authorization_domain,
              (SELECT count(*) FROM cellbridge_lists.memberships m WHERE m.workspace_id=w.workspace_id),
              (SELECT count(*) FROM cellbridge_lists.list_grants g WHERE g.workspace_id=w.workspace_id)
            FROM cellbridge_lists.workspaces w WHERE w.workspace_id=$1
            """);
        command.Parameters.AddWithValue(workspaceId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2), reader.GetInt64(3));
    }

    private async Task AssertRebindingRejectedAsync(Guid workspaceId, string? replacementDomain)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            database.SetWorkspaceContextAsync(workspaceId, async (connection, transaction) =>
            {
                await using var update = new NpgsqlCommand("UPDATE cellbridge_lists.workspaces SET authorization_domain=$2 WHERE workspace_id=$1", connection, transaction);
                update.Parameters.AddWithValue(workspaceId);
                update.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Text,
                    Value = (object?)replacementDomain ?? DBNull.Value
                });
                await update.ExecuteNonQueryAsync();
            }));
        Assert.Equal("23514", exception.SqlState);
    }
}
