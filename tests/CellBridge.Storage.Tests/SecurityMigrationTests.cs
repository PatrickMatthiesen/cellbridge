using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class SecurityMigrationTests
{
    [PostgreSqlFact]
    public Task LegacyVersion1RequiresOwnerAndPreservesContent() => MigrateLegacyAsync(1);

    [PostgreSqlFact]
    public Task MainVersion2RequiresOwnerAndPreservesContent() => MigrateLegacyAsync(2);

    private static async Task MigrateLegacyAsync(int priorSchema)
    {
        var original = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        var schema = "migration_" + Guid.NewGuid().ToString("N");
        await using var administration = NpgsqlDataSource.Create(original);
        await using (var create = administration.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            var isolated = new NpgsqlConnectionStringBuilder(original) { SearchPath = schema }.ConnectionString;
            await using var source = NpgsqlDataSource.Create(isolated);
            var states = new PostgreSqlStateStore(source);
            await states.InitializeAsync();
            var provider = new StorageProvider(states, new InMemoryContentStore());
            var document = (await new CellBridgeDocumentService(provider).CreateAsync("/shared/legacy.docx", MinimalDocx.Create(), TestActor.Value))!;
            var owner = new SubjectIdentity("local:migration-owner", "owner", "Owner");
            var legacy = document with { FormatVersion = 1,
                Editors = [new(Guid.NewGuid(), "anonymous", DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddHours(1),
                    3600, true, 1, ImmutableDictionary<string, ImmutableArray<byte>>.Empty)],
                Coordination = new("legacy-schema", [new("lock", "client", DateTime.UtcNow.AddHours(1), 0, "legacy-schema")],
                    new("exclusive", "client", DateTime.UtcNow.AddHours(1), 1, null), 1),
                Receipts = [new("operation", "digest", document.ContentVersion, document.Content, "tests:legacy")] };
            await using (var update = source.CreateCommand("UPDATE cellbridge_states SET state_json=$1"))
            { update.Parameters.AddWithValue(JsonSerializer.Serialize(legacy)); await update.ExecuteNonQueryAsync(); }
            await using (var history = source.CreateCommand($"INSERT INTO cellbridge_states SELECT resource_id,1,state_json FROM cellbridge_states; UPDATE cellbridge_documents SET state_version=1; UPDATE cellbridge_schema SET version={priorSchema}"))
                await history.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageUnavailableException>(() => states.InitializeAsync(legacyOwner: owner, allowUpgrade: false));
            await Assert.ThrowsAsync<InvalidOperationException>(() => states.InitializeAsync());
            await using (var unchanged = source.CreateCommand("SELECT version FROM cellbridge_schema"))
                Assert.Equal(priorSchema, await unchanged.ExecuteScalarAsync());
            await states.InitializeAsync(legacyOwner: owner);
            var current = (await states.FindByResourceIdAsync(document.ResourceId))!;
            Assert.Equal(document.Content, current.Content);
            Assert.Equal(document.ContentVersion, current.ContentVersion);
            Assert.Equal(owner.Subject, current.Security.Owner);
            Assert.Null(current.Security.CreatedBy);
            Assert.Null(current.Security.ModifiedBy);
            Assert.Empty(current.Editors);
            Assert.Empty(current.Coordination.SchemaOwners);
            Assert.Null(current.Coordination.Exclusive);
            await using (var ledger = source.CreateCommand("SELECT stored_bytes,COALESCE((SELECT SUM(length) FROM cellbridge_objects),0)+COALESCE((SELECT SUM(octet_length(state_json)) FROM cellbridge_states),0) FROM cellbridge_usage"))
            await using (var usage = await ledger.ExecuteReaderAsync())
            { Assert.True(await usage.ReadAsync()); Assert.Equal(usage.GetInt64(1), usage.GetInt64(0)); }
            await using var query = source.CreateCommand("SELECT state_json FROM cellbridge_states ORDER BY state_version");
            await using var rows = await query.ExecuteReaderAsync();
            int count = 0;
            while (await rows.ReadAsync())
            {
                var snapshot = JsonSerializer.Deserialize<DocumentState>(rows.GetString(0))!;
                Assert.Equal(DocumentState.CurrentFormat, snapshot.FormatVersion);
                Assert.Equal(owner.Subject, snapshot.Security.Owner);
                Assert.Empty(snapshot.Editors);
                Assert.All(snapshot.Receipts, r => Assert.Null(r.OwnerSubject));
                Assert.Equal(document.Content, snapshot.Content);
                count++;
            }
            Assert.Equal(2, count);
        }
        finally
        {
            await using var drop = administration.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
    [PostgreSqlFact]
    public Task AuthenticatedVersion2PreservesOwnershipAndRebuildsMissingLedger() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var states = new PostgreSqlStateStore(source);
            var provider = new StorageProvider(states, new PostgreSqlContentStore(source));
            var document = (await new CellBridgeDocumentService(provider).CreateAsync(
                "/shared/authenticated.docx", MinimalDocx.Create(), TestActor.Value))!;
            var reader = new SubjectIdentity("local:reader", "reader", "Reader");
            await states.TransitionAsync(document.ResourceId, (state, _) => new StateTransition<bool>(
                state with { Security = state.Security with { Grants = state.Security.Grants.SetItem(reader.Subject, DocumentAccess.Read) },
                    Receipts = [new("operation", "digest", state.ContentVersion, state.Content, TestActor.Value.Identity.Subject)] }, true));
            var before = (await states.FindByResourceIdAsync(document.ResourceId))!;
            await using (var downgrade = source.CreateCommand("UPDATE cellbridge_schema SET version=2; DROP TABLE cellbridge_usage"))
                await downgrade.ExecuteNonQueryAsync();
            await states.InitializeAsync();
            var after = (await states.FindByResourceIdAsync(document.ResourceId))!;
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
            await using var ledger = source.CreateCommand("SELECT stored_bytes,COALESCE((SELECT SUM(length) FROM cellbridge_objects),0)+COALESCE((SELECT SUM(octet_length(state_json)) FROM cellbridge_states),0) FROM cellbridge_usage");
            await using var usage = await ledger.ExecuteReaderAsync();
            Assert.True(await usage.ReadAsync());
            Assert.Equal(usage.GetInt64(1), usage.GetInt64(0));
        });

}
