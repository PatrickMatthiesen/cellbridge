using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.Storage.PostgreSql.Migrations;

internal sealed partial class BaselineSchema6 : Migration
{
    internal const string MigrationId = "20261022000100_BaselineSchema6";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(PostgreSqlSchema.BaselineSchema6);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "CellBridge storage migrations do not delete persisted document data.");
}

internal static class PostgreSqlSchema
{
    internal const string BaselineSchema6 = """
        CREATE TABLE cellbridge_schema (version integer PRIMARY KEY);
        INSERT INTO cellbridge_schema VALUES (6);
        CREATE TABLE cellbridge_documents (
            resource_id uuid PRIMARY KEY,
            path_key text COLLATE "C" NOT NULL,
            state_version bigint NOT NULL,
            is_deleted boolean NOT NULL DEFAULT false
        );
        CREATE UNIQUE INDEX cellbridge_live_paths ON cellbridge_documents(path_key) WHERE NOT is_deleted;
        CREATE TABLE cellbridge_states (
            resource_id uuid NOT NULL,
            state_version bigint NOT NULL,
            state_json text NOT NULL,
            PRIMARY KEY (resource_id, state_version)
        );
        CREATE TABLE cellbridge_read_heads (
            resource_id uuid PRIMARY KEY,
            state_version bigint NOT NULL,
            lifecycle_generation bigint NOT NULL,
            revision_count integer NOT NULL CHECK (revision_count > 0),
            metadata_json text NOT NULL
        );
        CREATE TABLE cellbridge_revision_summaries (
            resource_id uuid NOT NULL,
            lifecycle_generation bigint NOT NULL,
            revision_number numeric(20,0) NOT NULL CHECK (revision_number BETWEEN 1 AND 18446744073709551615),
            summary_json text NOT NULL,
            PRIMARY KEY (resource_id, lifecycle_generation, revision_number)
        );
        CREATE TABLE cellbridge_objects (
            object_key text PRIMARY KEY,
            length bigint NOT NULL CHECK (length >= 0),
            sha256 text NOT NULL
        );
        CREATE TABLE cellbridge_chunks (
            object_key text NOT NULL REFERENCES cellbridge_objects(object_key),
            ordinal integer NOT NULL,
            bytes bytea NOT NULL,
            PRIMARY KEY (object_key, ordinal)
        );
        CREATE TABLE cellbridge_usage (
            singleton boolean PRIMARY KEY CHECK (singleton),
            stored_bytes bigint NOT NULL CHECK (stored_bytes >= 0),
            document_count bigint NOT NULL CHECK (document_count >= 0),
            max_stored_bytes bigint NOT NULL CHECK (max_stored_bytes > 0),
            max_documents bigint NOT NULL CHECK (max_documents > 0)
        );
        INSERT INTO cellbridge_usage VALUES (true,0,0,10737418240,10000);
        CREATE TABLE cellbridge_recovery_schema(version integer PRIMARY KEY);
        INSERT INTO cellbridge_recovery_schema VALUES (1);
        CREATE TABLE cellbridge_recovery_receipts(operation_id uuid PRIMARY KEY,receipt_json text NOT NULL);
        """;
}
