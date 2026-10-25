using CellBridge.Lists;
using CellBridge.Lists.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.SqlServer.Tests;

public sealed partial class SqlServerListEngineTests
{
    [Theory]
    [InlineData("scalar_key", "ALTER")]
    [InlineData("scalar_key", "CONTROL")]
    [InlineData("scalar_key", "TAKE OWNERSHIP")]
    [InlineData("uuid_key", "ALTER")]
    [InlineData("uuid_key", "CONTROL")]
    [InlineData("uuid_key", "TAKE OWNERSHIP")]
    [InlineData("key_compare", "ALTER")]
    [InlineData("key_compare", "CONTROL")]
    [InlineData("key_compare", "TAKE OWNERSHIP")]
    public async Task QueryFunctionModificationPermissionsFailHealthAndOperations(string function, string permission)
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        await env.Engine.CheckHealthAsync();
        var user = new SqlConnectionStringBuilder(env.Runtime).UserID;
        await using var admin = new SqlServerListDbContext(env.AdminOptions);
        var connection = (SqlConnection)admin.Database.GetDbConnection();
        await admin.Database.OpenConnectionAsync();
        await using (var grant = new SqlCommand(
            $"GRANT {permission} ON OBJECT::cellbridge_lists.[{function}] TO [{user}]", connection))
            await grant.ExecuteNonQueryAsync();

        await Failure(ListFailure.Unavailable, () => env.Engine.CheckHealthAsync());
        await Failure(ListFailure.Unavailable, () => env.Engine.QueryAsync(Admin, env.Workspace, list.Id, new()));

        await using (var revoke = new SqlCommand(
            $"REVOKE {permission} ON OBJECT::cellbridge_lists.[{function}] FROM [{user}]", connection))
            await revoke.ExecuteNonQueryAsync();
        await env.Engine.CheckHealthAsync();
        await env.Engine.QueryAsync(Admin, env.Workspace, list.Id, new());
    }
}
