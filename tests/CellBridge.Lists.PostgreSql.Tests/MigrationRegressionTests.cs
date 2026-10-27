using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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
            var fixture = await CreatePopulatedVersionOneAsync(ownerSource);

            await PostgreSqlListSetup.InitializeAsync(ownerSource, runtimeRole);
            await AssertMigratedAsync(ownerSource, fixture);
            await PostgreSqlListSetup.InitializeAsync(ownerSource, runtimeRole);
            await AssertMigratedAsync(ownerSource, fixture);

            var runtimeConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Database = databaseName,
                Username = runtimeRole,
                Password = runtimePassword,
                ApplicationName = "CellBridge.Lists.MigrationRuntime"
            }.ConnectionString;
            await using var runtimeSource = NpgsqlDataSource.Create(runtimeConnectionString);
            var engine = new PostgreSqlListEngine(runtimeSource, new ListLimits
            {
                MaxWorkspaceBytes = fixture.LegacyMigratedBytes - 1
            });
            await engine.CheckHealthAsync();

            var actor = new ListActor(fixture.LegacySubject);
            await engine.SetListGrantAsync(actor, fixture.LegacyWorkspace, fixture.SourceList,
                fixture.LegacySubject, ListAccess.None);
            var grantBytes = 80 + Encoding.UTF8.GetByteCount(fixture.LegacySubject);
            Assert.Equal(fixture.LegacyMigratedBytes - grantBytes,
                await GetWorkspaceBytesAsync(ownerSource, fixture.LegacyWorkspace));

            var quotaFailure = await Assert.ThrowsAsync<ListException>(() => engine.SetListGrantAsync(actor,
                fixture.LegacyWorkspace, fixture.SourceList, fixture.LegacySubject, ListAccess.Read));
            Assert.Equal(ListFailure.Limit, quotaFailure.Failure);

            await engine.SetMembershipAsync(actor, fixture.LegacyWorkspace, fixture.LegacySubject,
                false, false, false);
            Assert.Equal(fixture.LegacyNonAuthorityBytes,
                await GetWorkspaceBytesAsync(ownerSource, fixture.LegacyWorkspace));
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

    [PostgreSqlFact]
    public async Task GenuineVersionThreeFixtureIsAdoptedWithoutDataLossAndInitializationIsExplicit()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var databaseName = "lists_schema3_" + suffix;
        var ownerRole = "lists_schema3_owner_" + suffix;
        var runtimeRole = "lists_schema3_runtime_" + suffix;
        var ownerPassword = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var quotedDatabase = QuoteIdentifier(databaseName);
        var quotedOwner = QuoteIdentifier(ownerRole);
        var quotedRuntime = QuoteIdentifier(runtimeRole);
        var databaseCreated = false;
        try
        {
            await using (var command = database.AdminSource!.CreateCommand($"""
                CREATE ROLE {quotedOwner} LOGIN PASSWORD '{ownerPassword}' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
                CREATE ROLE {quotedRuntime} NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
                """))
                await command.ExecuteNonQueryAsync();
            await using (var command = database.AdminSource!.CreateCommand(
                             $"CREATE DATABASE {quotedDatabase} OWNER {quotedOwner}"))
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
                ApplicationName = "CellBridge.Lists.Schema3Owner"
            }.ConnectionString;
            await using var ownerSource = NpgsqlDataSource.Create(ownerConnectionString);
            var schema = await ReadVersionThreeSchemaAsync();
            Assert.Equal("58f0bf90e50d742258f2eecc33141ca24be842c73f583655d1dcc1e3b956bad0",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema))).ToLowerInvariant());
            await using (var create = ownerSource.CreateCommand(schema))
                await create.ExecuteNonQueryAsync();
            var fixture = await SeedVersionThreeAsync(ownerSource);
            await AssertVersionThreeRowsAsync(ownerSource, fixture);
            await AssertVersionAndHistoryAsync(ownerSource, 3, expectHistory: false);

            var uninitializedEngine = new PostgreSqlListEngine(ownerSource);
            await Assert.ThrowsAnyAsync<Exception>(() => uninitializedEngine.CheckHealthAsync());
            await AssertVersionAndHistoryAsync(ownerSource, 3, expectHistory: false);

            await using (var inconsistentHistory = ownerSource.CreateCommand("""
                CREATE TABLE cellbridge_lists."__CellBridgeListsMigrationsHistory" (
                  "MigrationId" character varying(150) PRIMARY KEY,
                  "ProductVersion" character varying(32) NOT NULL);
                INSERT INTO cellbridge_lists."__CellBridgeListsMigrationsHistory" ("MigrationId","ProductVersion")
                VALUES ('99999999999999_FutureListsSchema','99.0.0');
                """))
                await inconsistentHistory.ExecuteNonQueryAsync();
            var legacyHistoryError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PostgreSqlListSetup.InitializeAsync(ownerSource, runtimeRole));
            Assert.Contains("legacy Lists schema", legacyHistoryError.Message, StringComparison.Ordinal);
            await AssertVersionAndHistoryRowsAsync(ownerSource, 3,
                ["99999999999999_FutureListsSchema"]);
            await using (var restoreLegacyFixture = ownerSource.CreateCommand(
                             "DROP TABLE cellbridge_lists.\"__CellBridgeListsMigrationsHistory\""))
                await restoreLegacyFixture.ExecuteNonQueryAsync();

            await PostgreSqlListSetup.InitializeAsync(ownerSource, runtimeRole);
            await AssertVersionThreeRowsAsync(ownerSource, fixture);
            await AssertVersionAndHistoryAsync(ownerSource, 4, expectHistory: true);
            await PostgreSqlListSetup.InitializeAsync(ownerSource, runtimeRole);
            await AssertVersionThreeRowsAsync(ownerSource, fixture);
            await AssertVersionAndHistoryAsync(ownerSource, 4, expectHistory: true);

            // ProductVersion records the EF tool version, not the library schema.
            // Patch upgrades accept the same migrations without rewriting their metadata.
            foreach (var productVersion in new[] { "10.0.4", "10.0.13" })
            {
                await using (var metadata = ownerSource.CreateCommand("""
                    UPDATE cellbridge_lists."__CellBridgeListsMigrationsHistory"
                    SET "ProductVersion"=$1
                    """))
                {
                    metadata.Parameters.AddWithValue(productVersion);
                    Assert.Equal(3, await metadata.ExecuteNonQueryAsync());
                }
                await PostgreSqlListSetup.InitializeAsync(ownerSource, runtimeRole);
                await AssertVersionThreeRowsAsync(ownerSource, fixture);
                await using var preserved = ownerSource.CreateCommand("""
                    SELECT count(*) FROM cellbridge_lists."__CellBridgeListsMigrationsHistory"
                    WHERE "ProductVersion"=$1
                    """);
                preserved.Parameters.AddWithValue(productVersion);
                Assert.Equal(3L, await preserved.ExecuteScalarAsync());
                await AssertVersionAndHistoryAsync(ownerSource, 4, expectHistory: true);
            }

            await using (var removeHistory = ownerSource.CreateCommand("""
                DELETE FROM cellbridge_lists."__CellBridgeListsMigrationsHistory"
                WHERE "MigrationId"='20261022040000_EfQueryFunctions'
                """))
                Assert.Equal(1, await removeHistory.ExecuteNonQueryAsync());
            var currentHistoryError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PostgreSqlListSetup.InitializeAsync(ownerSource, runtimeRole));
            Assert.Contains("does not match schema version 4", currentHistoryError.Message,
                StringComparison.Ordinal);
            await AssertVersionAndHistoryRowsAsync(ownerSource, 4,
                ["20261022033144_ListsSchemaV3", "20261027090000_WriteReservations"]);
        }
        finally
        {
            if (databaseCreated)
            {
                await using var dropDatabase = database.AdminSource!.CreateCommand(
                    $"DROP DATABASE IF EXISTS {quotedDatabase} WITH (FORCE)");
                await dropDatabase.ExecuteNonQueryAsync();
            }
            await using var dropRoles = database.AdminSource!.CreateCommand($"""
                DROP ROLE IF EXISTS {quotedRuntime};
                DROP ROLE IF EXISTS {quotedOwner};
                """);
            await dropRoles.ExecuteNonQueryAsync();
        }
    }

    private static async Task<VersionThreeFixture> SeedVersionThreeAsync(NpgsqlDataSource ownerSource)
    {
        var workspace = Guid.Parse("61000000-0000-0000-0000-000000000001");
        var list = Guid.Parse("62000000-0000-0000-0000-000000000001");
        var field = Guid.Parse("63000000-0000-0000-0000-000000000001");
        var record = Guid.Parse("64000000-0000-0000-0000-000000000001");
        var view = Guid.Parse("65000000-0000-0000-0000-000000000001");
        const string subject = "schema3:administrator:blåbær:😀";
        const string receiptActor = "schema3:receipt:æ";
        const string digest = "schema3:digest:ø";
        const string value = "Preserved blåbær 😀";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var definition = JsonSerializer.Serialize(new ListDefinition(list, workspace, "Schema 3", 1,
            [new ListField(field, "Title", ListFieldType.Text)]), options);
        var values = JsonSerializer.Serialize(new Dictionary<string, string>
            { [field.ToString("D")] = value }, options);

        await using var connection = await ownerSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT set_config('cellbridge.workspace_id',@workspace_text,true);
            INSERT INTO cellbridge_lists.workspaces(
              workspace_id,name,stored_bytes,audit_count,security_audit_count)
            VALUES (@workspace,'Schema 3 workspace',9876,1,0);
            INSERT INTO cellbridge_lists.memberships(
              workspace_id,subject,can_create_lists,can_manage_grants)
            VALUES (@workspace,@subject,true,true);
            INSERT INTO cellbridge_lists.lists(
              workspace_id,list_id,name,schema_version,next_number)
            VALUES (@workspace,@list,'Schema 3 list',1,8);
            INSERT INTO cellbridge_lists.list_schemas(
              workspace_id,list_id,schema_version,definition)
            VALUES (@workspace,@list,1,@definition::jsonb);
            INSERT INTO cellbridge_lists.list_counters(
              workspace_id,list_id,identity_count,next_number)
            VALUES (@workspace,@list,1,8);
            INSERT INTO cellbridge_lists.list_grants(workspace_id,list_id,subject,access)
            VALUES (@workspace,@list,@subject,7);
            INSERT INTO cellbridge_lists.records(
              workspace_id,list_id,record_id,schema_version,revision,number,values_json,
              created_utc,modified_utc,created_by,modified_by)
            VALUES (@workspace,@list,@record,1,3,7,@values::jsonb,
              '2026-01-02T03:04:05Z','2026-02-03T04:05:06Z',@subject,@subject);
            INSERT INTO cellbridge_lists.record_tombstones(workspace_id,list_id,record_id)
            VALUES (@workspace,@list,@record);
            INSERT INTO cellbridge_lists.create_receipts(
              workspace_id,list_id,record_id,actor,schema_version,request_digest,revision,number,
              created_utc,expires_utc)
            VALUES (@workspace,@list,@record,@receipt_actor,1,@digest,3,7,
              '2026-02-03T04:05:06Z','2099-02-03T04:05:06Z');
            INSERT INTO cellbridge_lists.saved_views(workspace_id,list_id,view_id,definition)
            VALUES (@workspace,@list,@view,'{"name":"Blå view","columns":[]}'::jsonb);
            INSERT INTO cellbridge_lists.audit_events(
              workspace_id,utc,actor,action,list_id,record_id,revision,field_ids,target_subject,security)
            VALUES (@workspace,'2026-02-03T04:05:07Z',@subject,'record.update',@list,@record,3,
              ARRAY[@field]::uuid[],NULL,false);
            """, connection, transaction);
        command.Parameters.AddWithValue("workspace_text", workspace.ToString("D"));
        command.Parameters.AddWithValue("workspace", workspace);
        command.Parameters.AddWithValue("list", list);
        command.Parameters.AddWithValue("field", field);
        command.Parameters.AddWithValue("record", record);
        command.Parameters.AddWithValue("view", view);
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("receipt_actor", receiptActor);
        command.Parameters.AddWithValue("digest", digest);
        command.Parameters.AddWithValue("definition", definition);
        command.Parameters.AddWithValue("values", values);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return new VersionThreeFixture(workspace, list, field, record, view, subject, receiptActor, digest, value);
    }

    private static async Task AssertVersionThreeRowsAsync(NpgsqlDataSource ownerSource, VersionThreeFixture fixture)
    {
        await using var connection = await ownerSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
                         "SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction))
        {
            context.Parameters.AddWithValue(fixture.Workspace.ToString("D"));
            await context.ExecuteNonQueryAsync();
        }
        await using (var workspace = new NpgsqlCommand("""
            SELECT name,stored_bytes,audit_count,security_audit_count
            FROM cellbridge_lists.workspaces WHERE workspace_id=$1
            """, connection, transaction))
        {
            workspace.Parameters.AddWithValue(fixture.Workspace);
            await using var reader = await workspace.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Schema 3 workspace", reader.GetString(0));
            Assert.Equal(9876L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(0, reader.GetInt32(3));
        }
        await using (var rows = new NpgsqlCommand("""
            SELECT l.name,l.schema_version,c.identity_count,c.next_number,r.revision,r.number,
              r.values_json->>$2,cr.actor,cr.request_digest,v.definition->>'name',a.action,a.field_ids
            FROM cellbridge_lists.lists l
            JOIN cellbridge_lists.list_counters c USING(workspace_id,list_id)
            JOIN cellbridge_lists.records r USING(workspace_id,list_id)
            JOIN cellbridge_lists.create_receipts cr USING(workspace_id,list_id,record_id)
            JOIN cellbridge_lists.saved_views v USING(workspace_id,list_id)
            JOIN cellbridge_lists.audit_events a USING(workspace_id,list_id,record_id)
            WHERE l.workspace_id=$1 AND l.list_id=$3 AND r.record_id=$4 AND v.view_id=$5
            """, connection, transaction))
        {
            rows.Parameters.AddWithValue(fixture.Workspace);
            rows.Parameters.AddWithValue(fixture.Field.ToString("D"));
            rows.Parameters.AddWithValue(fixture.List);
            rows.Parameters.AddWithValue(fixture.Record);
            rows.Parameters.AddWithValue(fixture.View);
            await using var reader = await rows.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Schema 3 list", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(8L, reader.GetInt64(3));
            Assert.Equal(3L, reader.GetInt64(4));
            Assert.Equal(7L, reader.GetInt64(5));
            Assert.Equal(fixture.Value, reader.GetString(6));
            Assert.Equal(fixture.ReceiptActor, reader.GetString(7));
            Assert.Equal(fixture.Digest, reader.GetString(8));
            Assert.Equal("Blå view", reader.GetString(9));
            Assert.Equal("record.update", reader.GetString(10));
            Assert.Equal([fixture.Field], reader.GetFieldValue<Guid[]>(11));
            Assert.False(await reader.ReadAsync());
        }
        await using var tombstone = new NpgsqlCommand("""
            SELECT count(*) FROM cellbridge_lists.record_tombstones
            WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3
            """, connection, transaction);
        tombstone.Parameters.AddWithValue(fixture.Workspace);
        tombstone.Parameters.AddWithValue(fixture.List);
        tombstone.Parameters.AddWithValue(fixture.Record);
        Assert.Equal(1L, await tombstone.ExecuteScalarAsync());
        await transaction.CommitAsync();
    }

    private static async Task AssertVersionAndHistoryAsync(NpgsqlDataSource source, int expectedVersion,
        bool expectHistory)
    {
        await using var version = source.CreateCommand("SELECT version FROM cellbridge_lists.schema_version");
        Assert.Equal(expectedVersion, await version.ExecuteScalarAsync());
        await using var historyExists = source.CreateCommand(
            "SELECT to_regclass('cellbridge_lists.\"__CellBridgeListsMigrationsHistory\"') IS NOT NULL");
        Assert.Equal(expectHistory, await historyExists.ExecuteScalarAsync());
        if (!expectHistory) return;
        await using var history = source.CreateCommand("""
            SELECT array_agg("MigrationId" ORDER BY "MigrationId")
            FROM cellbridge_lists."__CellBridgeListsMigrationsHistory"
            """);
        Assert.Equal(new[] { "20261022033144_ListsSchemaV3", "20261022040000_EfQueryFunctions", "20261027090000_WriteReservations" },
            (string[])(await history.ExecuteScalarAsync())!);
    }

    private static async Task AssertVersionAndHistoryRowsAsync(NpgsqlDataSource source, int expectedVersion,
        string[] expectedHistory)
    {
        await using var version = source.CreateCommand("SELECT version FROM cellbridge_lists.schema_version");
        Assert.Equal(expectedVersion, await version.ExecuteScalarAsync());
        await using var history = source.CreateCommand("""
            SELECT array_agg("MigrationId" ORDER BY "MigrationId")
            FROM cellbridge_lists."__CellBridgeListsMigrationsHistory"
            """);
        Assert.Equal(expectedHistory, (string[])(await history.ExecuteScalarAsync())!);
    }

    private static async Task<string> ReadVersionThreeSchemaAsync()
    {
        await using var stream = typeof(MigrationRegressionTests).Assembly.GetManifestResourceStream(
            "CellBridge.Lists.PostgreSql.Tests.Fixtures.postgresql-schema3.sql")
            ?? throw new InvalidOperationException("The frozen Lists schema 3 fixture is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    private static async Task<MigrationFixture> CreatePopulatedVersionOneAsync(NpgsqlDataSource ownerSource)
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
              PRIMARY KEY(workspace_id,list_id,subject),
              FOREIGN KEY(workspace_id,list_id) REFERENCES cellbridge_lists.lists(workspace_id,list_id) ON DELETE CASCADE,
              FOREIGN KEY(workspace_id,subject) REFERENCES cellbridge_lists.memberships(workspace_id,subject) ON DELETE CASCADE);
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
              field_ids uuid[] NOT NULL DEFAULT '{}',target_subject text NULL,security boolean NOT NULL DEFAULT false,
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
        var correctedWorkspace = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var correctedList = Guid.Parse("20000000-0000-0000-0000-000000000003");
        var correctedTitle = new ListField(Guid.Parse("30000000-0000-0000-0000-000000000003"),
            "Corrected", ListFieldType.Text);
        var correctedDefinition = new ListDefinition(correctedList, correctedWorkspace, "Corrected", 1,
            [correctedTitle]);
        const string legacySubject = "migration:administrator:blåbær:😀";
        const string correctedSubject = "migration:corrected:smørrebrød";
        const string receiptActor = "migration:receipt:æ";
        const string receiptDigest = "digest:ø";
        const string auditActor = "migration:audit:å";
        const string auditAction = "record.review";
        const string auditTarget = "migration:target:é";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var targetDefinitionJson = JsonSerializer.Serialize(targetDefinition, options);
        var sourceDefinitionJson = JsonSerializer.Serialize(sourceDefinition, options);
        var correctedDefinitionJson = JsonSerializer.Serialize(correctedDefinition, options);
        var targetValuesJson = JsonSerializer.Serialize(new Dictionary<string, string>
            { [title.Id.ToString("D")] = "target" }, options);
        var sourceValuesJson = JsonSerializer.Serialize(new Dictionary<string, string>
            { [reference.Id.ToString("D")] = targetRecord.ToString("D") }, options);
        const string savedViewJson = """{"name":"Blå","columns":[]}""";
        await using var populateConnection = await ownerSource.OpenConnectionAsync();
        await using var populate = new NpgsqlBatch(populateConnection);
        var insertWorkspace = new NpgsqlBatchCommand(
            "INSERT INTO cellbridge_lists.workspaces(workspace_id,name) VALUES (@workspace,'Migration'),(@corrected_workspace,'Corrected')");
        insertWorkspace.Parameters.AddWithValue("workspace", workspace);
        insertWorkspace.Parameters.AddWithValue("corrected_workspace", correctedWorkspace);
        populate.BatchCommands.Add(insertWorkspace);
        var insertLists = new NpgsqlBatchCommand("""
            INSERT INTO cellbridge_lists.lists(workspace_id,list_id,name,schema_version,next_number)
            VALUES (@workspace,@target_list,'Targets',1,6),(@workspace,@source_list,'Sources',1,42),
              (@corrected_workspace,@corrected_list,'Corrected',1,1)
            """);
        insertLists.Parameters.AddWithValue("workspace", workspace);
        insertLists.Parameters.AddWithValue("target_list", targetList);
        insertLists.Parameters.AddWithValue("source_list", sourceList);
        insertLists.Parameters.AddWithValue("corrected_workspace", correctedWorkspace);
        insertLists.Parameters.AddWithValue("corrected_list", correctedList);
        populate.BatchCommands.Add(insertLists);
        var insertSchemas = new NpgsqlBatchCommand("""
            INSERT INTO cellbridge_lists.list_schemas(workspace_id,list_id,schema_version,definition)
            VALUES (@workspace,@target_list,1,@target_definition::jsonb),
              (@workspace,@source_list,1,@source_definition::jsonb),
              (@corrected_workspace,@corrected_list,1,@corrected_definition::jsonb)
            """);
        insertSchemas.Parameters.AddWithValue("workspace", workspace);
        insertSchemas.Parameters.AddWithValue("target_list", targetList);
        insertSchemas.Parameters.AddWithValue("source_list", sourceList);
        insertSchemas.Parameters.AddWithValue("corrected_workspace", correctedWorkspace);
        insertSchemas.Parameters.AddWithValue("corrected_list", correctedList);
        insertSchemas.Parameters.AddWithValue("target_definition", targetDefinitionJson);
        insertSchemas.Parameters.AddWithValue("source_definition", sourceDefinitionJson);
        insertSchemas.Parameters.AddWithValue("corrected_definition", correctedDefinitionJson);
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
        insertRecords.Parameters.AddWithValue("target_values", targetValuesJson);
        insertRecords.Parameters.AddWithValue("source_values", sourceValuesJson);
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
        var insertMemberships = new NpgsqlBatchCommand("""
            INSERT INTO cellbridge_lists.memberships(workspace_id,subject,can_create_lists,can_manage_grants)
            VALUES (@workspace,@legacy_subject,true,true),(@corrected_workspace,@corrected_subject,true,true)
            """);
        insertMemberships.Parameters.AddWithValue("workspace", workspace);
        insertMemberships.Parameters.AddWithValue("corrected_workspace", correctedWorkspace);
        insertMemberships.Parameters.AddWithValue("legacy_subject", legacySubject);
        insertMemberships.Parameters.AddWithValue("corrected_subject", correctedSubject);
        populate.BatchCommands.Add(insertMemberships);
        var insertGrants = new NpgsqlBatchCommand("""
            INSERT INTO cellbridge_lists.list_grants(workspace_id,list_id,subject,access)
            VALUES (@workspace,@target_list,@legacy_subject,7),(@workspace,@source_list,@legacy_subject,7),
              (@corrected_workspace,@corrected_list,@corrected_subject,7)
            """);
        insertGrants.Parameters.AddWithValue("workspace", workspace);
        insertGrants.Parameters.AddWithValue("corrected_workspace", correctedWorkspace);
        insertGrants.Parameters.AddWithValue("target_list", targetList);
        insertGrants.Parameters.AddWithValue("source_list", sourceList);
        insertGrants.Parameters.AddWithValue("corrected_list", correctedList);
        insertGrants.Parameters.AddWithValue("legacy_subject", legacySubject);
        insertGrants.Parameters.AddWithValue("corrected_subject", correctedSubject);
        populate.BatchCommands.Add(insertGrants);
        var insertReceipt = new NpgsqlBatchCommand("""
            INSERT INTO cellbridge_lists.create_receipts(workspace_id,list_id,record_id,actor,schema_version,
              request_digest,revision,number,created_utc,expires_utc)
            VALUES (@workspace,@target_list,@target_record,@actor,1,@digest,1,5,clock_timestamp(),
              clock_timestamp()+interval '1 day')
            """);
        insertReceipt.Parameters.AddWithValue("workspace", workspace);
        insertReceipt.Parameters.AddWithValue("target_list", targetList);
        insertReceipt.Parameters.AddWithValue("target_record", targetRecord);
        insertReceipt.Parameters.AddWithValue("actor", receiptActor);
        insertReceipt.Parameters.AddWithValue("digest", receiptDigest);
        populate.BatchCommands.Add(insertReceipt);
        var insertView = new NpgsqlBatchCommand("""
            INSERT INTO cellbridge_lists.saved_views(workspace_id,list_id,view_id,definition)
            VALUES (@workspace,@source_list,@view,@definition::jsonb)
            """);
        insertView.Parameters.AddWithValue("workspace", workspace);
        insertView.Parameters.AddWithValue("source_list", sourceList);
        insertView.Parameters.AddWithValue("view", Guid.Parse("50000000-0000-0000-0000-000000000001"));
        insertView.Parameters.AddWithValue("definition", savedViewJson);
        populate.BatchCommands.Add(insertView);
        var insertAudits = new NpgsqlBatchCommand("""
            INSERT INTO cellbridge_lists.audit_events(workspace_id,utc,actor,action,list_id,record_id,revision,
              field_ids,target_subject,security)
            VALUES (@workspace,clock_timestamp(),@actor,@action,@source_list,@source_record,1,
                ARRAY[@field]::uuid[],@target,false),
              (@workspace,clock_timestamp(),@actor,'grant.reduce',@source_list,NULL,NULL,
                '{}'::uuid[],@target,true)
            """);
        insertAudits.Parameters.AddWithValue("workspace", workspace);
        insertAudits.Parameters.AddWithValue("actor", auditActor);
        insertAudits.Parameters.AddWithValue("action", auditAction);
        insertAudits.Parameters.AddWithValue("source_list", sourceList);
        insertAudits.Parameters.AddWithValue("source_record", sourceRecord);
        insertAudits.Parameters.AddWithValue("field", reference.Id);
        insertAudits.Parameters.AddWithValue("target", auditTarget);
        populate.BatchCommands.Add(insertAudits);
        await populate.ExecuteNonQueryAsync();

        var legacyNonAuthorityBytes = 2 * 160L + 2 * 96L + 3 * 128L +
            await CanonicalJsonBytesAsync(populateConnection, targetDefinitionJson, sourceDefinitionJson,
                targetValuesJson, sourceValuesJson) +
            192 + Encoding.UTF8.GetByteCount(receiptActor) + Encoding.UTF8.GetByteCount(receiptDigest) +
            64 + await CanonicalJsonBytesAsync(populateConnection, savedViewJson) +
            96 + Encoding.UTF8.GetByteCount(auditActor) + Encoding.UTF8.GetByteCount(auditAction) +
            Encoding.UTF8.GetByteCount(auditTarget) + 16;
        var legacySubjectBytes = Encoding.UTF8.GetByteCount(legacySubject);
        var legacyMigratedBytes = legacyNonAuthorityBytes + 128 + legacySubjectBytes +
            2 * (80 + legacySubjectBytes);
        var correctedSubjectBytes = Encoding.UTF8.GetByteCount(correctedSubject);
        var correctedBytes = 160L + 96L +
            await CanonicalJsonBytesAsync(populateConnection, correctedDefinitionJson) +
            128 + correctedSubjectBytes + 80 + correctedSubjectBytes;
        await using (var usage = new NpgsqlCommand("""
            UPDATE cellbridge_lists.workspaces
            SET stored_bytes=@legacy_bytes,audit_count=1,security_audit_count=1 WHERE workspace_id=@workspace;
            UPDATE cellbridge_lists.workspaces SET stored_bytes=@corrected_bytes WHERE workspace_id=@corrected_workspace
            """, populateConnection))
        {
            usage.Parameters.AddWithValue("legacy_bytes", legacyNonAuthorityBytes);
            usage.Parameters.AddWithValue("workspace", workspace);
            usage.Parameters.AddWithValue("corrected_bytes", correctedBytes);
            usage.Parameters.AddWithValue("corrected_workspace", correctedWorkspace);
            await usage.ExecuteNonQueryAsync();
        }

        await using var force = ownerSource.CreateCommand("""
            DO $force$
            DECLARE table_name text;
            BEGIN
              FOREACH table_name IN ARRAY ARRAY['workspaces','memberships','lists','list_schemas','list_grants',
                'records','record_tombstones','create_receipts','saved_views','audit_events'] LOOP
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
        return new MigrationFixture(workspace, sourceList, legacySubject, legacyNonAuthorityBytes,
            legacyMigratedBytes, correctedWorkspace, correctedBytes);
    }

    private static async Task AssertMigratedAsync(NpgsqlDataSource ownerSource, MigrationFixture fixture)
    {
        await using var version = ownerSource.CreateCommand("SELECT version FROM cellbridge_lists.schema_version");
        Assert.Equal(4, await version.ExecuteScalarAsync());
        await using var history = ownerSource.CreateCommand("""
            SELECT "MigrationId" FROM cellbridge_lists."__CellBridgeListsMigrationsHistory"
            ORDER BY "MigrationId"
            """);
        await using (var historyReader = await history.ExecuteReaderAsync())
        {
            Assert.True(await historyReader.ReadAsync());
            Assert.Equal("20261022033144_ListsSchemaV3", historyReader.GetString(0));
            Assert.True(await historyReader.ReadAsync());
            Assert.Equal("20261022040000_EfQueryFunctions", historyReader.GetString(0));
            Assert.True(await historyReader.ReadAsync());
            Assert.Equal("20261027090000_WriteReservations", historyReader.GetString(0));
            Assert.False(await historyReader.ReadAsync());
        }
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
        await using (var usage = new NpgsqlCommand("SELECT stored_bytes FROM cellbridge_lists.workspaces WHERE workspace_id=$1", connection, transaction))
        {
            usage.Parameters.AddWithValue(fixture.LegacyWorkspace);
            Assert.Equal(fixture.LegacyMigratedBytes, await usage.ExecuteScalarAsync());
        }
        await using (var context = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction))
        {
            context.Parameters.AddWithValue(fixture.CorrectedWorkspace.ToString("D"));
            await context.ExecuteNonQueryAsync();
        }
        await using (var usage = new NpgsqlCommand("SELECT stored_bytes FROM cellbridge_lists.workspaces WHERE workspace_id=$1", connection, transaction))
        {
            usage.Parameters.AddWithValue(fixture.CorrectedWorkspace);
            Assert.Equal(fixture.CorrectedBytes, await usage.ExecuteScalarAsync());
        }
        await using (var forced = new NpgsqlCommand("""
            SELECT bool_and(c.relforcerowsecurity)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='cellbridge_lists' AND c.relname=ANY($1)
            """, connection, transaction))
        {
            forced.Parameters.AddWithValue(new[] { "workspaces", "memberships", "lists", "list_schemas",
                "list_counters", "list_grants", "records", "record_tombstones", "create_receipts",
                "record_references", "saved_views", "audit_events" });
            Assert.Equal(true, await forced.ExecuteScalarAsync());
        }
        await transaction.CommitAsync();
    }

    private static async Task<long> CanonicalJsonBytesAsync(NpgsqlConnection connection, params string[] jsons)
    {
        long bytes = 0;
        foreach (var json in jsons)
        {
            await using var command = new NpgsqlCommand("SELECT octet_length(($1::jsonb)::text)::bigint", connection);
            command.Parameters.AddWithValue(json);
            bytes += (long)(await command.ExecuteScalarAsync())!;
        }
        return bytes;
    }

    private static async Task<long> GetWorkspaceBytesAsync(NpgsqlDataSource source, Guid workspace)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction))
        {
            context.Parameters.AddWithValue(workspace.ToString("D"));
            await context.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(
            "SELECT stored_bytes FROM cellbridge_lists.workspaces WHERE workspace_id=$1", connection, transaction);
        command.Parameters.AddWithValue(workspace);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record MigrationFixture(Guid LegacyWorkspace, Guid SourceList, string LegacySubject,
        long LegacyNonAuthorityBytes, long LegacyMigratedBytes, Guid CorrectedWorkspace, long CorrectedBytes);

    private sealed record VersionThreeFixture(Guid Workspace, Guid List, Guid Field, Guid Record, Guid View,
        string Subject, string ReceiptActor, string Digest, string Value);

    private static string QuoteIdentifier(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
}
