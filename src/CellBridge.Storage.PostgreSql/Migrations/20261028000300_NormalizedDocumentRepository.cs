using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.Storage.PostgreSql.Migrations;

[DbContext(typeof(CellBridgeDbContext))]
[Migration(MigrationId)]
internal sealed class NormalizedDocumentRepository : Migration
{
    internal const string MigrationId = "20261028000300_NormalizedDocumentRepository";
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cellbridge_repository_heads (
            "PathKey" text COLLATE "C" NOT NULL,
            "ResourceId" uuid NOT NULL,
            "PathHash" varchar(64) NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "StateVersion" bigint NOT NULL,
            "IsDeleted" boolean NOT NULL,
            "PendingDeliveries" integer NOT NULL,
            "DeliveryBlocked" boolean NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_heads" PRIMARY KEY ("ResourceId")
        );
        CREATE INDEX "IX_repository_heads_0" ON cellbridge_repository_heads ("IsDeleted", "PathHash");
        CREATE INDEX "IX_repository_heads_1" ON cellbridge_repository_heads ("IsDeleted", "DeliveryBlocked", "PendingDeliveries", "ResourceId");
        CREATE TABLE cellbridge_repository_coordination (
            "ResourceId" uuid NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_coordination" PRIMARY KEY ("ResourceId")
        );
        CREATE TABLE cellbridge_repository_editors (
            "ResourceId" uuid NOT NULL,
            "ClientId" uuid NOT NULL,
            "ExpiresUtc" timestamp with time zone NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_editors" PRIMARY KEY ("ResourceId", "ClientId")
        );
        CREATE INDEX "IX_repository_editors_0" ON cellbridge_repository_editors ("ResourceId", "ExpiresUtc");
        CREATE TABLE cellbridge_repository_partitions (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "Sequence" bigint NOT NULL,
            "CheckpointSequence" bigint NOT NULL,
            "ReplayChanges" integer NOT NULL,
            "ReplayBytes" bigint NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_partitions" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition")
        );
        CREATE TABLE cellbridge_repository_elements (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "ElementKey" varchar(64) NOT NULL,
            "SerialGuid" uuid NOT NULL,
            "SerialValue" numeric(20,0) NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_elements" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition", "ElementKey")
        );
        CREATE INDEX "IX_repository_elements_0" ON cellbridge_repository_elements ("ResourceId", "Lifecycle", "Partition", "SerialGuid", "SerialValue");
        CREATE TABLE cellbridge_repository_visibility (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "ElementKey" varchar(64) NOT NULL,
            "StartSequence" bigint NOT NULL,
            "EndSequence" bigint NULL,
            CONSTRAINT "PK_repository_visibility" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition", "ElementKey", "StartSequence")
        );
        CREATE INDEX "IX_repository_visibility_0" ON cellbridge_repository_visibility ("ResourceId", "Lifecycle", "Partition", "EndSequence", "StartSequence");
        CREATE TABLE cellbridge_repository_graph (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "Kind" varchar(16) NOT NULL,
            "OwnerKey" varchar(64) NOT NULL,
            "RecordKey" varchar(64) NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_graph" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition", "Kind", "OwnerKey", "RecordKey")
        );
        CREATE TABLE cellbridge_repository_bindings (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "BindingKey" varchar(64) NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_bindings" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition", "BindingKey")
        );
        CREATE TABLE cellbridge_repository_binding_changes (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "Sequence" bigint NOT NULL,
            "BindingKey" varchar(64) NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_binding_changes" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition", "Sequence", "BindingKey")
        );
        CREATE TABLE cellbridge_repository_checkpoints (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "Sequence" bigint NOT NULL,
            "BindingKey" varchar(64) NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_checkpoints" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition", "Sequence", "BindingKey")
        );
        CREATE TABLE cellbridge_repository_revisions (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "RevisionNumber" numeric(20,0) NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_revisions" PRIMARY KEY ("ResourceId", "Lifecycle", "RevisionNumber")
        );
        CREATE TABLE cellbridge_repository_receipts (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Family" integer NOT NULL,
            "Partition" integer NOT NULL,
            "OperationHash" varchar(64) NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_receipts" PRIMARY KEY ("ResourceId", "Lifecycle", "Family", "Partition", "OperationHash")
        );
        CREATE TABLE cellbridge_repository_paths (
            "PathHash" varchar(64) NOT NULL,
            "PathKey" text COLLATE "C" NOT NULL,
            "ResourceId" uuid NOT NULL,
            CONSTRAINT "PK_repository_paths" PRIMARY KEY ("PathHash")
        );
        CREATE INDEX "IX_repository_paths_0" ON cellbridge_repository_paths ("ResourceId");
        CREATE TABLE cellbridge_repository_deliveries (
            "ResourceId" uuid NOT NULL,
            "OperationId" uuid NOT NULL,
            "Sequence" bigint NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_deliveries" PRIMARY KEY ("ResourceId", "OperationId")
        );
        CREATE UNIQUE INDEX "IX_repository_deliveries_0" ON cellbridge_repository_deliveries ("ResourceId", "Sequence");
        CREATE TABLE cellbridge_repository_references (
            "ResourceId" uuid NOT NULL,
            "ObjectKey" varchar(64) NOT NULL,
            "Length" bigint NOT NULL,
            "Sha256" varchar(64) NOT NULL,
            "ReferenceCount" bigint NOT NULL,
            CONSTRAINT "PK_repository_references" PRIMARY KEY ("ResourceId", "ObjectKey")
        );
        CREATE TABLE cellbridge_repository_deltas (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "Sequence" bigint NOT NULL,
            "PreviousSequence" bigint NOT NULL,
            "EncodedBytes" bigint NOT NULL,
            "ChangeCount" integer NOT NULL,
            CONSTRAINT "PK_repository_deltas" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition", "Sequence")
        );
        CREATE TABLE cellbridge_repository_mappings (
            "StartSequence" bigint NOT NULL,
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "MappingKey" varchar(64) NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_mappings" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition", "MappingKey")
        );
        CREATE TABLE cellbridge_repository_mapping_intervals (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "MappingKey" varchar(64) NOT NULL,
            "StartSequence" bigint NOT NULL,
            "EndSequence" bigint NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_mapping_intervals" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition", "MappingKey", "StartSequence")
        );
        CREATE TABLE cellbridge_repository_mapping_roots (
            "Json" text NOT NULL,
            "SerialGuid" uuid NOT NULL,
            "SerialValue" numeric(20,0) NOT NULL,
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "IndexKey" varchar(64) NOT NULL,
            "Sequence" bigint NOT NULL,
            CONSTRAINT "PK_repository_mapping_roots" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition", "IndexKey")
        );
        CREATE TABLE cellbridge_repository_mapping_claims (
            "ResourceId" uuid NOT NULL,
            "Lifecycle" bigint NOT NULL,
            "Partition" integer NOT NULL,
            "SerialGuid" uuid NOT NULL,
            "SerialValue" numeric(20,0) NOT NULL,
            "Json" text NOT NULL,
            CONSTRAINT "PK_repository_mapping_claims" PRIMARY KEY ("ResourceId", "Lifecycle", "Partition", "SerialGuid", "SerialValue")
        );
        CREATE INDEX "IX_repository_mapping_intervals_0" ON cellbridge_repository_mapping_intervals ("ResourceId", "Lifecycle", "Partition", "EndSequence", "StartSequence");
        CREATE INDEX "IX_repository_mapping_roots_0" ON cellbridge_repository_mapping_roots ("ResourceId", "Lifecycle", "Partition", "Sequence");
        CREATE INDEX "IX_repository_visibility_1" ON cellbridge_repository_visibility ("ResourceId", "Lifecycle", "Partition", "ElementKey", "EndSequence");
        UPDATE cellbridge_schema SET version=9 WHERE version=8;
        """);
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => CellBridgeDbContext.ConfigureModel(modelBuilder);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Removing normalized document publications is not supported.");
}
