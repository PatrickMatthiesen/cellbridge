using System.Text.Json;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class MigrationRegressionTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task VersionOneForcedRlsDataMigratesUnderANonBypassOwnerAndSetupIsIdempotent()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var databaseName = "lists_migration_" + suffix;
        var ownerRole = "lists_owner_" + suffix;
        var runtimeRole = "lists_runtime_" + suffix;
        var ownerPassword = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var runtimePassword = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var quotedDatabase = QuoteIdentifier(databaseName);
        var quotedOwner = QuoteIdentifier(ownerRole);
        var quotedRuntime = QuoteIdentifier(runtimeRole);
        var databaseCreated = false;
        try
        {
            await using (var command = database.AdminSource!.CreateCommand($"""
                CREATE ROLE {quotedOwner} LOGIN PASSWORD '{ownerPassword}' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
                CREATE ROLE {quotedRuntime} LOGIN PASSWORD '{runtimePassword}' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
                """))
                await command.ExecuteNonQueryAsync();
            await using (var command = database.AdminSource!.CreateCommand($"CREATE DATABASE {quotedDatabase} OWNER {quotedOwner}"))
                await command.ExecuteNonQueryAsync();
            databaseCreated = true;

            var baseConnectionString = Environment.GetEnvironmentVariable("CELLBRIDGE_LISTS_TEST_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("ConnectionStrings__lists")
                ?? throw new InvalidOperationException("The PostgreSQL test connection is missing.");
            var ownerConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Database = databaseName,
                Username = ownerRole,
                Password = ownerPassword,
                ApplicationName = "CellBridge.Lists.MigrationOwner"
            }.ConnectionString;
            await using var ownerSource = NpgsqlDataSource.Create(ownerConnectionString);
            await CreatePopulatedVersionOneAsync(ownerSource);

            await PostgreSqlListSetup.InitializeAsync(ownerSource, runtimeRole);
            await AssertMigratedAsync(ownerSource);
            await PostgreSqlListSetup.InitializeAsync(ownerSource, runtimeRole);
            await AssertMigratedAsync(ownerSource);

            var runtimeConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Database = databaseName,
                Username = runtimeRole,
                Password = runtimePassword,
                ApplicationName = "CellBridge.Lists.MigrationRuntime"
            }.ConnectionString;
            await using var runtimeSource = NpgsqlDataSource.Create(runtimeConnectionString);
            await new PostgreSqlListEngine(runtimeSource).CheckHealthAsync();
        }
        finally
        {
            if (databaseCreated)
            {
                await using var dropDatabase = database.AdminSource!.CreateCommand($"DROP DATABASE IF EXISTS {quotedDatabase} WITH (FORCE)");
                await dropDatabase.ExecuteNonQueryAsync();
            }
            await using var dropRoles = database.AdminSource!.CreateCommand($"""
                DROP ROLE IF EXISTS {quotedRuntime};
                DROP ROLE IF EXISTS {quotedOwner};
                """);
            await dropRoles.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreatePopulatedVersionOneAsync(NpgsqlDataSource ownerSource)
    {
        await using var schema = ownerSource.CreateCommand("""
            CREATE SCHEMA cellbridge_lists;
            CREATE TABLE cellbridge_lists.schema_version (
              singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),version integer NOT NULL);
            INSERT INTO cellbridge_lists.schema_version VALUES (true,1);
            CREATE TABLE cellbridge_lists.templates (
              template_id uuid NOT NULL,version bigint NOT NULL,definition jsonb NOT NULL,
              PRIMARY KEY(template_id,version));
            CREATE TABLE cellbridge_lists.workspaces (
              workspace_id uuid PRIMARY KEY,name text NOT NULL,stored_bytes bigint NOT NULL DEFAULT 0,
              audit_count integer NOT NULL DEFAULT 0,security_audit_count integer NOT NULL DEFAULT 0);
            CREATE TABLE cellbridge_lists.memberships (
              workspace_id uuid NOT NULL REFERENCES cellbridge_lists.workspaces(workspace_id) ON DELETE CASCADE,
              subject text NOT NULL,can_create_lists boolean NOT NULL DEFAULT false,
              can_manage_grants boolean NOT NULL DEFAULT false,PRIMARY KEY(workspace_id,subject));
            CREATE TABLE cellbridge_lists.lists (
              workspace_id uuid NOT NULL REFERENCES cellbridge_lists.workspaces(workspace_id) ON DELETE CASCADE,
              list_id uuid NOT NULL,name text NOT NULL,schema_version bigint NOT NULL,template_id uuid NULL,
              template_version bigint NULL,number_prefix text NULL,next_number bigint NOT NULL DEFAULT 1,
              PRIMARY KEY(workspace_id,list_id));
            CREATE TABLE cellbridge_lists.list_schemas (
              workspace_id uuid NOT NULL,list_id uuid NOT NULL,schema_version bigint NOT NULL,definition jsonb NOT NULL,
              PRIMARY KEY(workspace_id,list_id,schema_version),FOREIGN KEY(workspace_id,list_id)
                REFERENCES cellbridge_lists.lists(workspace_id,list_id) ON DELETE CASCADE);
            CREATE TABLE cellbridge_lists.list_grants (
              workspace_id uuid NOT NULL,list_id uuid NOT NULL,subject text NOT NULL,access integer NOT NULL,
              PRIMARY KEY(workspace_id,list_id,subject));
            CREATE TABLE cellbridge_lists.records (
              workspace_id uuid NOT NULL,list_id uuid NOT NULL,record_id uuid NOT NULL,schema_version bigint NOT NULL,
              revision bigint NOT NULL,number bigint NOT NULL,values_json jsonb NOT NULL,created_utc timestamptz NOT NULL,
              modified_utc timestamptz NOT NULL,created_by text NOT NULL,modified_by text NOT NULL,
              PRIMARY KEY(workspace_id,list_id,record_id),UNIQUE(workspace_id,list_id,number),
              FOREIGN KEY(workspace_id,list_id,schema_version)
                REFERENCES cellbridge_lists.list_schemas(workspace_id,list_id,schema_version));
            CREATE TABLE cellbridge_lists.record_tombstones (
              workspace_id uuid NOT NULL,list_id uuid NOT NULL,record_id uuid NOT NULL,
              PRIMARY KEY(workspace_id,list_id,record_id));
            CREATE TABLE cellbridge_lists.create_receipts (
              workspace_id uuid NOT NULL,list_id uuid NOT NULL,record_id uuid NOT NULL,actor text NOT NULL,
              schema_version bigint NOT NULL,request_digest text NOT NULL,revision bigint NOT NULL,number bigint NOT NULL,
              created_utc timestamptz NOT NULL,expires_utc timestamptz NOT NULL,
              PRIMARY KEY(workspace_id,list_id,record_id));
            CREATE TABLE cellbridge_lists.saved_views (
              workspace_id uuid NOT NULL,list_id uuid NOT NULL,view_id uuid NOT NULL,definition jsonb NOT NULL,
              PRIMARY KEY(workspace_id,list_id,view_id));
            CREATE TABLE cellbridge_lists.audit_events (
              id bigint GENERATED ALWAYS AS IDENTITY,workspace_id uuid NOT NULL,utc timestamptz NOT NULL,
              actor text NOT NULL,action text NOT NULL,list_id uuid NULL,record_id uuid NULL,revision bigint NULL,
              field_ids uuid[] NOT NULL DEFAULT '{}',security boolean NOT NULL DEFAULT false,
              PRIMARY KEY(workspace_id,id));
            """);
        await schema.ExecuteNonQueryAsync();

        var workspace = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var targetList = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var sourceList = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var title = new ListField(Guid.Parse("30000000-0000-0000-0000-000000000001"), "Title", ListFieldType.Text);
        var reference = new ListField(Guid.Parse("30000000-0000-0000-0000-000000000002"), "Target",
            ListFieldType.Reference, ReferenceListId: targetList, ReferenceDisplayFieldId: title.Id);
        var targetDefinition = new ListDefinition(targetList, workspace, "Targets", 1, [title]);
        var sourceDefinition = new ListDefinition(sourceList, workspace, "Sources", 1, [reference]);
        var targetRecord = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var sourceRecord = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var deletedSource = Guid.Parse("40000000-0000-0000-0000-000000000003");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using var populateConnection = await ownerSource.OpenConnectionAsync();
        await using var populate = new NpgsqlBatch(populateConnection);
        var insertWorkspace = new NpgsqlBatchCommand(
            "INSERT INTO cellbridge_lists.workspaces(workspace_id,name) VALUES (@workspace,'Migration')");
        insertWorkspace.Parameters.AddWithValue("workspace", workspace);
        populate.BatchCommands.Add(insertWorkspace);
        var insertLists = new NpgsqlBatchCommand("""
            INSERT INTO cellbridge_lists.lists(workspace_id,list_id,name,schema_version,next_number)
            VALUES (@workspace,@target_list,'Targets',1,6),(@workspace,@source_list,'Sources',1,42)
            """);
        insertLists.Parameters.AddWithValue("workspace", workspace);
        insertLists.Parameters.AddWithValue("target_list", targetList);
        insertLists.Parameters.AddWithValue("source_list", sourceList);
        populate.BatchCommands.Add(insertLists);
        var insertSchemas = new NpgsqlBatchCommand("""
            INSERT INTO cellbridge_lists.list_schemas(workspace_id,list_id,schema_version,definition)
            VALUES (@workspace,@target_list,1,@target_definition::jsonb),
              (@workspace,@source_list,1,@source_definition::jsonb)
            """);
        insertSchemas.Parameters.AddWithValue("workspace", workspace);
        insertSchemas.Parameters.AddWithValue("target_list", targetList);
        insertSchemas.Parameters.AddWithValue("source_list", sourceList);
        insertSchemas.Parameters.AddWithValue("target_definition", JsonSerializer.Serialize(targetDefinition, options));
        insertSchemas.Parameters.AddWithValue("source_definition", JsonSerializer.Serialize(sourceDefinition, options));
        populate.BatchCommands.Add(insertSchemas);
        var insertRecords = new NpgsqlBatchCommand("""
            INSERT INTO cellbridge_lists.records(workspace_id,list_id,record_id,schema_version,revision,number,
              values_json,created_utc,modified_utc,created_by,modified_by)
            VALUES (@workspace,@target_list,@target_record,1,1,5,@target_values::jsonb,
                clock_timestamp(),clock_timestamp(),'owner','owner'),
              (@workspace,@source_list,@source_record,1,1,1,@source_values::jsonb,
                clock_timestamp(),clock_timestamp(),'owner','owner')
            """);
        insertRecords.Parameters.AddWithValue("workspace", workspace);
        insertRecords.Parameters.AddWithValue("target_list", targetList);
        insertRecords.Parameters.AddWithValue("source_list", sourceList);
        insertRecords.Parameters.AddWithValue("target_record", targetRecord);
        insertRecords.Parameters.AddWithValue("source_record", sourceRecord);
        insertRecords.Parameters.AddWithValue("target_values", JsonSerializer.Serialize(new Dictionary<string, string>
            { [title.Id.ToString("D")] = "target" }, options));
        insertRecords.Parameters.AddWithValue("source_values", JsonSerializer.Serialize(new Dictionary<string, string>
            { [reference.Id.ToString("D")] = targetRecord.ToString("D") }, options));
        populate.BatchCommands.Add(insertRecords);
        var insertTombstones = new NpgsqlBatchCommand("""
            INSERT INTO cellbridge_lists.record_tombstones(workspace_id,list_id,record_id)
            VALUES (@workspace,@target_list,@target_record),(@workspace,@source_list,@source_record),
              (@workspace,@source_list,@deleted_source)
            """);
        insertTombstones.Parameters.AddWithValue("workspace", workspace);
        insertTombstones.Parameters.AddWithValue("target_list", targetList);
        insertTombstones.Parameters.AddWithValue("source_list", sourceList);
        insertTombstones.Parameters.AddWithValue("target_record", targetRecord);
        insertTombstones.Parameters.AddWithValue("source_record", sourceRecord);
        insertTombstones.Parameters.AddWithValue("deleted_source", deletedSource);
        populate.BatchCommands.Add(insertTombstones);
        await populate.ExecuteNonQueryAsync();

        await using var force = ownerSource.CreateCommand("""
            DO $force$
            DECLARE table_name text;
            BEGIN
              FOREACH table_name IN ARRAY ARRAY['lists','list_schemas','records','record_tombstones'] LOOP
                EXECUTE format('ALTER TABLE cellbridge_lists.%I ENABLE ROW LEVEL SECURITY',table_name);
                EXECUTE format('ALTER TABLE cellbridge_lists.%I FORCE ROW LEVEL SECURITY',table_name);
                EXECUTE format($policy$CREATE POLICY workspace_isolation ON cellbridge_lists.%I
                  USING (workspace_id=NULLIF(current_setting('cellbridge.workspace_id',true),'')::uuid)
                  WITH CHECK (workspace_id=NULLIF(current_setting('cellbridge.workspace_id',true),'')::uuid)$policy$,table_name);
              END LOOP;
            END $force$;
            """);
        await force.ExecuteNonQueryAsync();
        await using var hidden = ownerSource.CreateCommand("SELECT count(*) FROM cellbridge_lists.records");
        Assert.Equal(0L, await hidden.ExecuteScalarAsync());
    }

    private static async Task AssertMigratedAsync(NpgsqlDataSource ownerSource)
    {
        await using var version = ownerSource.CreateCommand("SELECT version FROM cellbridge_lists.schema_version");
        Assert.Equal(2, await version.ExecuteScalarAsync());
        await using var connection = await ownerSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction))
        {
            context.Parameters.AddWithValue("10000000-0000-0000-0000-000000000001");
            await context.ExecuteNonQueryAsync();
        }
        await using (var counters = new NpgsqlCommand("""
            SELECT list_id,identity_count,next_number FROM cellbridge_lists.list_counters ORDER BY list_id
            """, connection, transaction))
        await using (var reader = await counters.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(1));
            Assert.Equal(6, reader.GetInt64(2));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt64(1));
            Assert.Equal(42, reader.GetInt64(2));
            Assert.False(await reader.ReadAsync());
        }
        await using (var references = new NpgsqlCommand("SELECT count(*) FROM cellbridge_lists.record_references", connection, transaction))
            Assert.Equal(1L, await references.ExecuteScalarAsync());
        await transaction.CommitAsync();
    }

    private static string QuoteIdentifier(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
}
