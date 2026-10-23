using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.Lists.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data;
using System.Data.Common;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerListsPersistenceTests(SqlServerFixture fixture)
{
    private DbContextOptions<SqlServerListDbContext> Options(string? connection = null) =>
        new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(connection ?? fixture.Lists,
            sql => sql.MigrationsHistoryTable(SqlServerListDbContext.MigrationsHistoryTable, SqlServerListDbContext.Schema)).Options;

    [Fact]
    public async Task IndependentMigrationsWorkspaceIsolationAndExactPersistedIdentities()
    {
        var options = Options();
        var migrator = new SqlServerListMigrator(options);
        await using (var untouched = new SqlServerListDbContext(options))
            Assert.Empty(await untouched.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, await SqlServerContentTests.ScalarAsync<int>(fixture.Lists,
            "SELECT COUNT(*) FROM sys.schemas WHERE name='cellbridge_lists'"));
        await Task.WhenAll(migrator.MigrateAsync(), new SqlServerListMigrator(options).MigrateAsync());
        await migrator.MigrateAsync();
        await using (var db = new SqlServerListDbContext(options))
        {
            Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.False(db.Database.HasPendingModelChanges());
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO cellbridge_lists.__CellBridgeListsMigrationsHistory VALUES ('20990101000000_Unknown','10.0.13')
                """);
            await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAsync());
            await db.Database.ExecuteSqlRawAsync("""
                DELETE FROM cellbridge_lists.__CellBridgeListsMigrationsHistory WHERE MigrationId='20990101000000_Unknown'
                """);
        }
        Assert.Equal(0, await SqlServerContentTests.ScalarAsync<int>(fixture.Lists,
            "SELECT COUNT(*) FROM sys.schemas WHERE name='cellbridge_documents'"));

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var list = Guid.NewGuid();
        var record = Guid.NewGuid();
        var subjects = new[] { "actor", "actor ", "ACTOR", "\U0001F680", new string('x', 1024) };
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["text"] = "\U0001F680 e\u0301 trailing ", ["integer"] = long.MaxValue,
            ["decimal"] = 1234567890123456.123456789012m, ["bool"] = true, ["null"] = null,
            ["long"] = new string('y', 5001)
        });
        foreach (var workspace in new[] { first, second })
        {
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(options, workspace);
            var db = scope.Context;
            Assert.Equal(workspace, await db.Database.SqlQueryRaw<Guid>(
                "SELECT CONVERT(uniqueidentifier,SUBSTRING(CONTEXT_INFO(),1,16)) AS Value").SingleAsync());
            Assert.Equal(128, await db.Database.SqlQueryRaw<int>("SELECT DATALENGTH(CONTEXT_INFO()) AS Value").SingleAsync());
            db.Workspaces.Add(new WorkspaceRow { WorkspaceId = workspace, Name = workspace.ToString() });
            await db.SaveChangesAsync();
            // Save separately because each server-generated digest is an immutable EF key.
            foreach (var subject in subjects)
            {
                db.Memberships.Add(new MembershipRow { WorkspaceId = workspace, Subject = subject });
                await db.SaveChangesAsync();
            }
            db.Lists.Add(new ListRow { WorkspaceId = workspace, ListId = list, Name = "Rows", SchemaVersion = 1 });
            await db.SaveChangesAsync();
            db.ListSchemas.Add(new ListSchemaRow { WorkspaceId = workspace, ListId = list, SchemaVersion = 1, Definition = "{}" });
            await db.SaveChangesAsync();
            db.Records.Add(new RecordRow { WorkspaceId = workspace, ListId = list, RecordId = record,
                SchemaVersion = 1, Revision = 1, Number = 1, ValuesJson = json,
                CreatedUtc = DateTimeOffset.UtcNow, ModifiedUtc = DateTimeOffset.UtcNow, CreatedBy = "actor", ModifiedBy = "actor" });
            await db.SaveChangesAsync();
            await scope.CommitAsync();
        }

        var runtime = await CreateRuntimeAsync();
        foreach (var sqlFailure in new[] { false, true })
            await AssertResetFailureDiscardsExternalSessionAsync(runtime, sqlFailure);
        foreach (var table in new[] { "memberships", "list_grants" })
        {
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(Options(runtime), first);
            var sql = table == "memberships"
                ? $"INSERT cellbridge_lists.memberships(workspace_id,subject) VALUES ('{first}',NULL)"
                : $"INSERT cellbridge_lists.list_grants(workspace_id,list_id,subject,access) VALUES ('{first}','{list}',NULL,1)";
            var error = await Assert.ThrowsAsync<SqlException>(() => scope.Context.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(515, error.Number);
        }
        Assert.Equal(0, await SqlServerContentTests.ScalarAsync<int>(runtime,
            "SELECT COUNT(*) FROM cellbridge_lists.records"));
        Assert.Equal(0, await SqlServerContentTests.ScalarAsync<int>(runtime,
            "SELECT COUNT(*) FROM cellbridge_lists.workspaces"));

        foreach (var workspace in new[] { first, second, first })
        {
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(Options(runtime), workspace);
            var db = scope.Context;
            var rows = await db.Records.AsNoTracking().ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(workspace, row.WorkspaceId);
            Assert.Equal(json, row.ValuesJson);
            using var parsed = JsonDocument.Parse(row.ValuesJson);
            Assert.Equal(9223372036854775807, parsed.RootElement.GetProperty("integer").GetInt64());
            Assert.Equal(1234567890123456.123456789012m, parsed.RootElement.GetProperty("decimal").GetDecimal());
            Assert.Equal("\U0001F680 e\u0301 trailing ", parsed.RootElement.GetProperty("text").GetString());
            Assert.Equal(5001, parsed.RootElement.GetProperty("long").GetString()!.Length);
            foreach (var subject in subjects)
                Assert.Equal(subject, (await db.Memberships.AsNoTracking().SingleAsync(member => member.Subject == subject)).Subject);
            Assert.Equal(5, await db.Memberships.CountAsync());
            Assert.Equal(0, await db.Records.CountAsync(r => r.WorkspaceId != workspace));
            await scope.CommitAsync();
        }
        // The same one-connection pool must not retain the last workspace.
        Assert.Equal(0, await SqlServerContentTests.ScalarAsync<int>(runtime,
            "SELECT COUNT(*) FROM cellbridge_lists.records"));
        Assert.Equal(0, await SqlServerContentTests.ScalarAsync<int>(runtime,
            "SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER ANY SECURITY POLICY')"));
        await using (var restricted = new SqlServerListDbContext(Options(runtime)))
        {
            await Assert.ThrowsAsync<SqlException>(() => restricted.Database.ExecuteSqlRawAsync(
                "CREATE TABLE cellbridge_lists.forbidden(id int)"));
            await Assert.ThrowsAsync<SqlException>(() => restricted.Database.ExecuteSqlRawAsync(
                "ALTER SECURITY POLICY cellbridge_lists.records_isolation WITH (STATE=OFF)"));
            await Assert.ThrowsAsync<SqlException>(() => new SqlServerListMigrator(Options(runtime)).MigrateAsync());
        }
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(Options(runtime), first))
        {
            var db = scope.Context;
            db.Workspaces.Add(new WorkspaceRow { WorkspaceId = Guid.NewGuid(), Name = "cross-workspace" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(Options(runtime), first))
        {
            // A transaction rollback does not clear CONTEXT_INFO. Scope disposal must do so.
            var row = await scope.Context.Records.SingleAsync();
            row.ValuesJson = "not JSON";
            await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());
        }
        // Validate optimistic revision predicates on two independent connections.
        var concurrent = new SqlConnectionStringBuilder(runtime) { MaxPoolSize = 2 }.ConnectionString;
        await using (var left = await SqlServerListWorkspaceTransaction.OpenAsync(Options(concurrent), first))
        await using (var right = await SqlServerListWorkspaceTransaction.OpenAsync(Options(concurrent), first))
        {
            var leftRow = await left.Context.Records.SingleAsync();
            var rightRow = await right.Context.Records.SingleAsync();
            leftRow.Revision++;
            await left.Context.SaveChangesAsync();
            await left.CommitAsync();
            rightRow.Revision++;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => right.Context.SaveChangesAsync());
        }
        Assert.Equal(0, await SqlServerContentTests.ScalarAsync<int>(runtime,
            "SELECT COUNT(*) FROM cellbridge_lists.records"));
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(Options(runtime), first))
        {
            var workspace = await scope.Context.Workspaces.SingleAsync();
            workspace.AuthorizationMode = "host";
            workspace.AuthorizationDomain = "domain";
            workspace.AuthorizationScope = "scope";
            await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());
        }
    }

    private async Task<string> CreateRuntimeAsync()
    {
        var password = "Cb!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var connection = new SqlConnection(fixture.Lists);
        await connection.OpenAsync();
        await using (var command = new SqlCommand($"""
            CREATE LOGIN cb_lists_runtime WITH PASSWORD='{password}';
            CREATE USER cb_lists_runtime FOR LOGIN cb_lists_runtime;
            GRANT SELECT ON cellbridge_lists.schema_version TO cb_lists_runtime;
            GRANT SELECT ON cellbridge_lists.templates TO cb_lists_runtime;
            """, connection)) await command.ExecuteNonQueryAsync();
        foreach (var table in new[] { "workspaces", "memberships", "lists", "list_schemas", "list_counters", "list_grants",
                     "records", "record_tombstones", "create_receipts", "record_references", "saved_views", "audit_events" })
        {
            await using var command = new SqlCommand($"GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_lists.[{table}] TO cb_lists_runtime", connection);
            await command.ExecuteNonQueryAsync();
        }
        var result = new SqlConnectionStringBuilder(fixture.Lists)
        { UserID = "cb_lists_runtime", Password = password, MaxPoolSize = 1 };
        Assert.Equal(0, await SqlServerContentTests.ScalarAsync<int>(result.ConnectionString,
            "SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONTROL')"));
        Assert.Equal(0, await SqlServerContentTests.ScalarAsync<int>(result.ConnectionString, "SELECT IS_SRVROLEMEMBER('sysadmin')"));
        return result.ConnectionString;
    }

    private async Task AssertResetFailureDiscardsExternalSessionAsync(string runtime, bool sqlFailure)
    {
        await using var connection = new SqlConnection(runtime);
        await connection.OpenAsync();
        var interceptor = new FailReset(sqlFailure);
        var options = new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(connection).AddInterceptors(interceptor).Options;
        var workspace = Guid.NewGuid();
        var scope = await SqlServerListWorkspaceTransaction.OpenAsync(options, workspace);
        scope.Context.Workspaces.Add(new() { WorkspaceId = workspace, Name = "committed before cleanup failure" });
        await scope.Context.SaveChangesAsync();
        await scope.CommitAsync();
        interceptor.Enabled = true;
        await scope.DisposeAsync();
        Assert.True(interceptor.Triggered);
        Assert.Equal(ConnectionState.Closed, connection.State);
        await connection.OpenAsync();
        await using (var unscoped = new SqlCommand("SELECT COUNT(*) FROM cellbridge_lists.workspaces", connection))
            Assert.Equal(0, await unscoped.ExecuteScalarAsync());
        // Rebind deliberately and prove cleanup did not turn the completed commit into a failed save.
        await using var rebound = await SqlServerListWorkspaceTransaction.OpenAsync(
            new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(connection).Options, workspace);
        Assert.Equal("committed before cleanup failure", (await rebound.Context.Workspaces.SingleAsync()).Name);
    }

    private sealed class FailReset(bool sqlFailure) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText == "SET CONTEXT_INFO 0x;")
            {
                Triggered = true;
                if (!sqlFailure) throw new IOException("Injected non-SQL cleanup failure.");
                command.CommandText = "THROW 51002, 'Injected SQL cleanup failure.', 1;";
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
