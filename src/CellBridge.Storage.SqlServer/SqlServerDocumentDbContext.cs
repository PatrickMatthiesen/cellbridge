using Microsoft.EntityFrameworkCore;
using System.Text;

namespace CellBridge.Storage.SqlServer;

/// <summary>Library-owned SQL Server document model, configured by the consumer.</summary>
public sealed partial class SqlServerDocumentDbContext(DbContextOptions<SqlServerDocumentDbContext> options) : DbContext(options)
{
    public const string Schema = "cellbridge_documents";
    public const string MigrationsHistoryTable = "__CellBridgeDocumentMigrationsHistory";
    internal DbSet<ObjectRow> Objects => Set<ObjectRow>();
    internal DbSet<ChunkRow> Chunks => Set<ChunkRow>();
    internal DbSet<UsageRow> Usage => Set<UsageRow>();
    internal DbSet<DocumentRow> Documents => Set<DocumentRow>();
    internal DbSet<StateRow> Snapshots => Set<StateRow>();
    internal DbSet<ReadHeadRow> Metadata => Set<ReadHeadRow>();
    internal DbSet<RevisionSummaryRow> Revisions => Set<RevisionSummaryRow>();
    internal DbSet<PathReservation> Reservations => Set<PathReservation>();
    internal DbSet<RecoveryReceiptRow> RecoveryReceipts => Set<RecoveryReceiptRow>();
    internal DbSet<DocumentSchema> SchemaVersions => Set<DocumentSchema>();
    internal static long ByteLength(string value) => throw new NotSupportedException("Database translation only.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<ObjectRow>(entity =>
        {
            entity.ToTable("objects", table => table.HasCheckConstraint("objects_length", "[Length] >= 0"));
            entity.HasKey(row => row.ObjectKey);
            entity.Property(row => row.ObjectKey).HasColumnName("Key").HasColumnType("char(64)").UseCollation("Latin1_General_100_BIN2");
            entity.Property(row => row.Sha256).HasColumnType("char(64)").UseCollation("Latin1_General_100_BIN2");
        });
        modelBuilder.Entity<ChunkRow>(entity =>
        {
            entity.ToTable("chunks", table =>
            {
                table.HasCheckConstraint("chunks_ordinal", "[Ordinal] >= 0");
                table.HasCheckConstraint("chunks_length", "DATALENGTH([Bytes]) BETWEEN 1 AND 65536");
            });
            entity.HasKey(row => new { row.ObjectKey, row.Ordinal });
            entity.Property(row => row.ObjectKey).HasColumnName("Key").HasColumnType("char(64)").UseCollation("Latin1_General_100_BIN2");
            entity.Property(row => row.Bytes).HasColumnType("varbinary(max)");
            entity.HasOne<ObjectRow>().WithMany().HasForeignKey(row => row.ObjectKey).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_chunks_objects_Key");
        });
        modelBuilder.Entity<UsageRow>(entity =>
        {
            entity.ToTable("usage", table =>
            {
                table.HasCheckConstraint("usage_singleton", "[Id] = 1");
                table.HasCheckConstraint("usage_bytes", "[StoredBytes] >= 0 AND [MaxStoredBytes] > 0");
            });
            entity.HasKey(row => row.Singleton);
            entity.Property(row => row.Singleton).HasColumnName("Id").HasConversion(value => value ? 1 : 0, value => value == 1).ValueGeneratedNever();
            entity.Property(row => row.DocumentCount).HasDefaultValue(0L);
            entity.Property(row => row.MaxDocuments).HasDefaultValue(10000L);
            entity.ToTable(table => table.HasCheckConstraint("usage_documents", "[DocumentCount] >= 0 AND [MaxDocuments] > 0"));
            entity.HasData(new UsageRow { Singleton = true, MaxStoredBytes = 10L * 1024 * 1024 * 1024, MaxDocuments = 10000 });
        });
        modelBuilder.HasDbFunction(() => DocumentSql.ByteLength(default!))
            .HasName("DATALENGTH").IsBuiltIn();
        modelBuilder.HasDbFunction(() => DocumentSql.ClockUtc())
            .HasName("SYSUTCDATETIME").IsBuiltIn();
        modelBuilder.HasDbFunction(() => ByteLength(default!)).HasName("DATALENGTH").IsBuiltIn();
        modelBuilder.Entity<DocumentRow>(entity =>
        {
            entity.ToTable("documents", table => table.HasCheckConstraint("documents_version", "[StateVersion] >= 0"));
            entity.HasKey(row => row.ResourceId);
            entity.Property(row => row.ResourceId).ValueGeneratedNever();
            // Big-endian UTF-16 gives exact identity equality and ordinal path ordering.
            entity.Property(row => row.PathKey).HasConversion(value => Encoding.BigEndianUnicode.GetBytes(value),
                value => Encoding.BigEndianUnicode.GetString(value)).HasColumnType("varbinary(max)");
            entity.Property(row => row.PathHash).HasColumnType("binary(32)");
            entity.HasIndex(row => row.PathHash);
            entity.Property(row => row.StateVersion).IsConcurrencyToken();
        });
        modelBuilder.Entity<StateRow>(entity =>
        {
            entity.Property(row => row.StateJson).HasColumnName("Data").HasConversion(value => DocumentEncoding.Encode(value), value => DocumentEncoding.Decode(value)).HasColumnType("varbinary(max)");
            entity.ToTable("snapshots", table => table.HasCheckConstraint("snapshots_version", "[StateVersion] >= 0"));
            entity.HasKey(row => new { row.ResourceId, row.StateVersion });
        });
        modelBuilder.Entity<ReadHeadRow>(entity =>
        {
            entity.Property(row => row.MetadataJson).HasColumnName("Data").HasConversion(value => DocumentEncoding.Encode(value), value => DocumentEncoding.Decode(value)).HasColumnType("varbinary(max)");
            entity.ToTable("metadata", table => table.HasCheckConstraint("metadata_revisions", "[RevisionCount] > 0 AND [LifecycleGeneration] > 0"));
            entity.HasKey(row => row.ResourceId);
            entity.Property(row => row.ResourceId).ValueGeneratedNever();
        });
        modelBuilder.Entity<RevisionSummaryRow>(entity =>
        {
            entity.Property(row => row.SummaryJson).HasColumnName("Data").HasConversion(value => DocumentEncoding.Encode(value), value => DocumentEncoding.Decode(value)).HasColumnType("varbinary(max)");
            entity.ToTable("revisions", table => table.HasCheckConstraint("revisions_number", "[RevisionNumber] BETWEEN 1 AND 18446744073709551615 AND [LifecycleGeneration] > 0"));
            entity.HasKey(row => new { row.ResourceId, row.LifecycleGeneration, row.RevisionNumber });
            entity.Property(row => row.RevisionNumber).HasPrecision(20, 0);
        });
        modelBuilder.Entity<PathReservation>(entity =>
        {
            entity.ToTable("reservations");
            entity.HasKey(row => new { row.ResourceId, row.Ordinal });
            entity.Property(row => row.PathKey).HasConversion(value => Encoding.BigEndianUnicode.GetBytes(value),
                value => Encoding.BigEndianUnicode.GetString(value)).HasColumnType("varbinary(max)");
            entity.Property(row => row.PathHash).HasColumnType("binary(32)");
            entity.HasIndex(row => row.PathHash);
        });
        modelBuilder.Entity<RecoveryReceiptRow>(entity =>
        {
            entity.Property(row => row.ReceiptJson).HasColumnName("Data").HasConversion(value => DocumentEncoding.Encode(value), value => DocumentEncoding.Decode(value)).HasColumnType("varbinary(max)");
            entity.ToTable("recovery_receipts");
            entity.HasKey(row => row.OperationId);
            entity.Property(row => row.OperationId).ValueGeneratedNever();
        });
        modelBuilder.Entity<DocumentSchema>(entity =>
        {
            entity.ToTable("schema_version", table => table.HasCheckConstraint("schema_singleton", "[Id] = 1"));
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.HasData(new DocumentSchema { Id = 1, Version = 1 });
        });
    }
}
