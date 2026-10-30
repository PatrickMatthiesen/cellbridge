using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.Storage.SqlServer.Migrations;

[DbContext(typeof(SqlServerDocumentDbContext))]
[Migration(MigrationId)]
internal sealed class NormalizedDocumentRepository : Migration
{
    internal const string MigrationId = "20261028140000_NormalizedDocumentRepository";
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cellbridge_documents.repository_heads (
            [PathKey] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [ResourceId] uniqueidentifier NOT NULL,
            [PathHash] varchar(64) NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [StateVersion] bigint NOT NULL,
            [IsDeleted] bit NOT NULL,
            [PendingDeliveries] int NOT NULL,
            [DeliveryBlocked] bit NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_heads] PRIMARY KEY ([ResourceId])
        );
        CREATE INDEX [IX_repository_heads_0] ON cellbridge_documents.repository_heads ([IsDeleted], [PathHash]);
        CREATE INDEX [IX_repository_heads_1] ON cellbridge_documents.repository_heads ([IsDeleted], [DeliveryBlocked], [PendingDeliveries], [ResourceId]);
        CREATE TABLE cellbridge_documents.repository_coordination (
            [ResourceId] uniqueidentifier NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_coordination] PRIMARY KEY ([ResourceId])
        );
        CREATE TABLE cellbridge_documents.repository_editors (
            [ResourceId] uniqueidentifier NOT NULL,
            [ClientId] uniqueidentifier NOT NULL,
            [ExpiresUtc] datetime2 NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_editors] PRIMARY KEY ([ResourceId], [ClientId])
        );
        CREATE INDEX [IX_repository_editors_0] ON cellbridge_documents.repository_editors ([ResourceId], [ExpiresUtc]);
        CREATE TABLE cellbridge_documents.repository_partitions (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [Sequence] bigint NOT NULL,
            [CheckpointSequence] bigint NOT NULL,
            [ReplayChanges] int NOT NULL,
            [ReplayBytes] bigint NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_partitions] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition])
        );
        CREATE TABLE cellbridge_documents.repository_elements (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [ElementKey] varchar(64) NOT NULL,
            [SerialGuid] uniqueidentifier NOT NULL,
            [SerialValue] decimal(20,0) NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_elements] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition], [ElementKey])
        );
        CREATE INDEX [IX_repository_elements_0] ON cellbridge_documents.repository_elements ([ResourceId], [Lifecycle], [Partition], [SerialGuid], [SerialValue]);
        CREATE TABLE cellbridge_documents.repository_visibility (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [ElementKey] varchar(64) NOT NULL,
            [StartSequence] bigint NOT NULL,
            [EndSequence] bigint NULL,
            CONSTRAINT [PK_repository_visibility] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition], [ElementKey], [StartSequence])
        );
        CREATE INDEX [IX_repository_visibility_0] ON cellbridge_documents.repository_visibility ([ResourceId], [Lifecycle], [Partition], [EndSequence], [StartSequence]);
        CREATE TABLE cellbridge_documents.repository_graph (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [Kind] varchar(16) NOT NULL,
            [OwnerKey] varchar(64) NOT NULL,
            [RecordKey] varchar(64) NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_graph] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition], [Kind], [OwnerKey], [RecordKey])
        );
        CREATE TABLE cellbridge_documents.repository_bindings (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [BindingKey] varchar(64) NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_bindings] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition], [BindingKey])
        );
        CREATE TABLE cellbridge_documents.repository_binding_changes (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [Sequence] bigint NOT NULL,
            [BindingKey] varchar(64) NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_binding_changes] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition], [Sequence], [BindingKey])
        );
        CREATE TABLE cellbridge_documents.repository_checkpoints (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [Sequence] bigint NOT NULL,
            [BindingKey] varchar(64) NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_checkpoints] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition], [Sequence], [BindingKey])
        );
        CREATE TABLE cellbridge_documents.repository_revisions (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [RevisionNumber] decimal(20,0) NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_revisions] PRIMARY KEY ([ResourceId], [Lifecycle], [RevisionNumber])
        );
        CREATE TABLE cellbridge_documents.repository_receipts (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Family] int NOT NULL,
            [Partition] int NOT NULL,
            [OperationHash] varchar(64) NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_receipts] PRIMARY KEY ([ResourceId], [Lifecycle], [Family], [Partition], [OperationHash])
        );
        CREATE TABLE cellbridge_documents.repository_paths (
            [PathHash] varchar(64) NOT NULL,
            [PathKey] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [ResourceId] uniqueidentifier NOT NULL,
            CONSTRAINT [PK_repository_paths] PRIMARY KEY ([PathHash])
        );
        CREATE INDEX [IX_repository_paths_0] ON cellbridge_documents.repository_paths ([ResourceId]);
        CREATE TABLE cellbridge_documents.repository_deliveries (
            [ResourceId] uniqueidentifier NOT NULL,
            [OperationId] uniqueidentifier NOT NULL,
            [Sequence] bigint NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_deliveries] PRIMARY KEY ([ResourceId], [OperationId])
        );
        CREATE UNIQUE INDEX [IX_repository_deliveries_0] ON cellbridge_documents.repository_deliveries ([ResourceId], [Sequence]);
        CREATE TABLE cellbridge_documents.repository_references (
            [ResourceId] uniqueidentifier NOT NULL,
            [ObjectKey] varchar(64) NOT NULL,
            [Length] bigint NOT NULL,
            [Sha256] varchar(64) NOT NULL,
            [ReferenceCount] bigint NOT NULL,
            CONSTRAINT [PK_repository_references] PRIMARY KEY ([ResourceId], [ObjectKey])
        );
        CREATE TABLE cellbridge_documents.repository_deltas (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [Sequence] bigint NOT NULL,
            [PreviousSequence] bigint NOT NULL,
            [EncodedBytes] bigint NOT NULL,
            [ChangeCount] int NOT NULL,
            CONSTRAINT [PK_repository_deltas] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition], [Sequence])
        );
        CREATE TABLE cellbridge_documents.repository_mappings (
            [StartSequence] bigint NOT NULL,
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [MappingKey] varchar(64) NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_mappings] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition], [MappingKey])
        );
        CREATE TABLE cellbridge_documents.repository_mapping_intervals (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [MappingKey] varchar(64) NOT NULL,
            [StartSequence] bigint NOT NULL,
            [EndSequence] bigint NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_mapping_intervals] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition], [MappingKey], [StartSequence])
        );
        CREATE TABLE cellbridge_documents.repository_mapping_roots (
            [Json] varbinary(max) NOT NULL,
            [SerialGuid] uniqueidentifier NOT NULL,
            [SerialValue] decimal(20,0) NOT NULL,
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [IndexKey] varchar(64) NOT NULL,
            [Sequence] bigint NOT NULL,
            CONSTRAINT [PK_repository_mapping_roots] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition], [IndexKey])
        );
        CREATE TABLE cellbridge_documents.repository_mapping_claims (
            [ResourceId] uniqueidentifier NOT NULL,
            [Lifecycle] bigint NOT NULL,
            [Partition] int NOT NULL,
            [SerialGuid] uniqueidentifier NOT NULL,
            [SerialValue] decimal(20,0) NOT NULL,
            [Json] varbinary(max) NOT NULL,
            CONSTRAINT [PK_repository_mapping_claims] PRIMARY KEY ([ResourceId], [Lifecycle], [Partition], [SerialGuid], [SerialValue])
        );
        CREATE INDEX [IX_repository_mapping_intervals_0] ON cellbridge_documents.repository_mapping_intervals ([ResourceId], [Lifecycle], [Partition], [EndSequence], [StartSequence]);
        CREATE INDEX [IX_repository_mapping_roots_0] ON cellbridge_documents.repository_mapping_roots ([ResourceId], [Lifecycle], [Partition], [Sequence]);
        CREATE INDEX [IX_repository_visibility_1] ON cellbridge_documents.repository_visibility ([ResourceId], [Lifecycle], [Partition], [ElementKey], [EndSequence]);
        UPDATE cellbridge_documents.schema_version SET Version=4 WHERE Id=1 AND Version=3;
        """);
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => SqlServerDocumentDbContext.ConfigureModel(modelBuilder);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Removing normalized document publications is not supported.");
}
