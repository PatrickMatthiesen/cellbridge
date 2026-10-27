using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.PostgreSql;

/// <summary>The library-owned EF Core context for CellBridge document storage.</summary>
public sealed class CellBridgeDbContext(DbContextOptions<CellBridgeDbContext> options) : DbContext(options)
{
    internal const string MigrationsHistoryTable = "__CellBridgeDocumentMigrationsHistory";
    internal DbSet<SchemaVersionRow> SchemaVersions => Set<SchemaVersionRow>();
    internal DbSet<DocumentRow> Documents => Set<DocumentRow>();
    internal DbSet<StateRow> States => Set<StateRow>();
    internal DbSet<ReadHeadRow> ReadHeads => Set<ReadHeadRow>();
    internal DbSet<RevisionSummaryRow> RevisionSummaries => Set<RevisionSummaryRow>();
    internal DbSet<ObjectRow> Objects => Set<ObjectRow>();
    internal DbSet<ChunkRow> Chunks => Set<ChunkRow>();
    internal DbSet<UsageRow> Usage => Set<UsageRow>();
    internal DbSet<RecoverySchemaRow> RecoverySchema => Set<RecoverySchemaRow>();
    internal DbSet<RecoveryReceiptRow> RecoveryReceipts => Set<RecoveryReceiptRow>();
    internal static int OctetLength(string value) => throw new InvalidOperationException("Database translation only.");

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel(modelBuilder);

    internal static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(() => DocumentSql.ClockUtc())
            .HasName("clock_timestamp").IsBuiltIn();
        modelBuilder.HasDbFunction(() => DocumentSql.ByteLength(default!))
            .HasName("octet_length").IsBuiltIn();
        modelBuilder.HasDbFunction(() => OctetLength(default!)).HasName("octet_length").IsBuiltIn();
        modelBuilder.Entity<SchemaVersionRow>(entity =>
        {
            entity.ToTable("cellbridge_schema");
            entity.HasKey(row => row.Version).HasName("cellbridge_schema_pkey");
            entity.Property(row => row.Version).HasColumnName("version").ValueGeneratedNever();
        });
        modelBuilder.Entity<DocumentRow>(entity =>
        {
            entity.ToTable("cellbridge_documents");
            entity.Ignore(row => row.PathHash);
            entity.Ignore(row => row.RetiredPathCount);
            entity.HasKey(row => row.ResourceId).HasName("cellbridge_documents_pkey");
            entity.Property(row => row.ResourceId).HasColumnName("resource_id").ValueGeneratedNever();
            entity.Property(row => row.PathKey).HasColumnName("path_key").UseCollation("C");
            entity.Property(row => row.StateVersion).HasColumnName("state_version").IsConcurrencyToken();
            entity.Property(row => row.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
            entity.HasIndex(row => row.PathKey).IsUnique().HasDatabaseName("cellbridge_live_paths")
                .HasFilter("NOT is_deleted");
        });
        modelBuilder.Entity<StateRow>(entity =>
        {
            entity.ToTable("cellbridge_states");
            entity.HasKey(row => new { row.ResourceId, row.StateVersion }).HasName("cellbridge_states_pkey");
            entity.Property(row => row.ResourceId).HasColumnName("resource_id").ValueGeneratedNever();
            entity.Property(row => row.StateVersion).HasColumnName("state_version");
            entity.Property(row => row.StateJson).HasColumnName("state_json");
        });
        modelBuilder.Entity<ReadHeadRow>(entity =>
        {
            entity.ToTable("cellbridge_read_heads", table => table.HasCheckConstraint(
                "cellbridge_read_heads_revision_count_check", "revision_count > 0"));
            entity.HasKey(row => row.ResourceId).HasName("cellbridge_read_heads_pkey");
            entity.Property(row => row.ResourceId).HasColumnName("resource_id").ValueGeneratedNever();
            entity.Property(row => row.StateVersion).HasColumnName("state_version");
            entity.Property(row => row.LifecycleGeneration).HasColumnName("lifecycle_generation");
            entity.Property(row => row.RevisionCount).HasColumnName("revision_count");
            entity.Property(row => row.MetadataJson).HasColumnName("metadata_json");
        });
        modelBuilder.Entity<RevisionSummaryRow>(entity =>
        {
            entity.ToTable("cellbridge_revision_summaries", table => table.HasCheckConstraint(
                "cellbridge_revision_summaries_revision_number_check",
                "revision_number BETWEEN 1 AND 18446744073709551615"));
            entity.HasKey(row => new { row.ResourceId, row.LifecycleGeneration, row.RevisionNumber })
                .HasName("cellbridge_revision_summaries_pkey");
            entity.Property(row => row.ResourceId).HasColumnName("resource_id");
            entity.Property(row => row.LifecycleGeneration).HasColumnName("lifecycle_generation");
            entity.Property(row => row.RevisionNumber).HasColumnName("revision_number").HasPrecision(20, 0);
            entity.Property(row => row.SummaryJson).HasColumnName("summary_json");
        });
        modelBuilder.Entity<ObjectRow>(entity =>
        {
            entity.ToTable("cellbridge_objects", table => table.HasCheckConstraint(
                "cellbridge_objects_length_check", "length >= 0"));
            entity.HasKey(row => row.ObjectKey).HasName("cellbridge_objects_pkey");
            entity.Property(row => row.ObjectKey).HasColumnName("object_key");
            entity.Property(row => row.Length).HasColumnName("length");
            entity.Property(row => row.Sha256).HasColumnName("sha256");
        });
        modelBuilder.Entity<ChunkRow>(entity =>
        {
            entity.ToTable("cellbridge_chunks");
            entity.HasKey(row => new { row.ObjectKey, row.Ordinal }).HasName("cellbridge_chunks_pkey");
            entity.Property(row => row.ObjectKey).HasColumnName("object_key");
            entity.Property(row => row.Ordinal).HasColumnName("ordinal");
            entity.Property(row => row.Bytes).HasColumnName("bytes");
            entity.HasOne<ObjectRow>().WithMany().HasForeignKey(row => row.ObjectKey)
                .OnDelete(DeleteBehavior.NoAction).HasConstraintName("cellbridge_chunks_object_key_fkey");
        });
        modelBuilder.Entity<UsageRow>(entity =>
        {
            entity.ToTable("cellbridge_usage", table =>
            {
                table.HasCheckConstraint("cellbridge_usage_singleton_check", "singleton");
                table.HasCheckConstraint("cellbridge_usage_stored_bytes_check", "stored_bytes >= 0");
                table.HasCheckConstraint("cellbridge_usage_document_count_check", "document_count >= 0");
                table.HasCheckConstraint("cellbridge_usage_max_stored_bytes_check", "max_stored_bytes > 0");
                table.HasCheckConstraint("cellbridge_usage_max_documents_check", "max_documents > 0");
            });
            entity.HasKey(row => row.Singleton).HasName("cellbridge_usage_pkey");
            entity.Property(row => row.Singleton).HasColumnName("singleton");
            entity.Property(row => row.StoredBytes).HasColumnName("stored_bytes");
            entity.Property(row => row.DocumentCount).HasColumnName("document_count");
            entity.Property(row => row.MaxStoredBytes).HasColumnName("max_stored_bytes");
            entity.Property(row => row.MaxDocuments).HasColumnName("max_documents");
        });
        modelBuilder.Entity<RecoverySchemaRow>(entity =>
        {
            entity.ToTable("cellbridge_recovery_schema");
            entity.HasKey(row => row.Version).HasName("cellbridge_recovery_schema_pkey");
            entity.Property(row => row.Version).HasColumnName("version").ValueGeneratedNever();
        });
        modelBuilder.Entity<RecoveryReceiptRow>(entity =>
        {
            entity.ToTable("cellbridge_recovery_receipts");
            entity.HasKey(row => row.OperationId).HasName("cellbridge_recovery_receipts_pkey");
            entity.Property(row => row.OperationId).HasColumnName("operation_id").ValueGeneratedNever();
            entity.Property(row => row.ReceiptJson).HasColumnName("receipt_json");
        });
    }
}
