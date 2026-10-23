using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text;

namespace CellBridge.Storage.SqlServer;

/// <summary>Library-owned SQL Server document model, configured by the consumer.</summary>
public sealed partial class SqlServerDocumentDbContext(DbContextOptions<SqlServerDocumentDbContext> options) : DbContext(options)
{
    public const string Schema = "cellbridge_documents";
    public const string MigrationsHistoryTable = "__CellBridgeDocumentMigrationsHistory";
    internal DbSet<ContentObject> Objects => Set<ContentObject>();
    internal DbSet<ContentChunk> Chunks => Set<ContentChunk>();
    internal DbSet<ContentUsage> Usage => Set<ContentUsage>();
    internal DbSet<DocumentPointer> Documents => Set<DocumentPointer>();
    internal DbSet<DocumentSnapshot> Snapshots => Set<DocumentSnapshot>();
    internal DbSet<DocumentMetadata> Metadata => Set<DocumentMetadata>();
    internal DbSet<RevisionMetadata> Revisions => Set<RevisionMetadata>();
    internal DbSet<PathReservation> Reservations => Set<PathReservation>();
    internal DbSet<RecoveryRecord> RecoveryReceipts => Set<RecoveryRecord>();
    internal DbSet<DocumentSchema> SchemaVersions => Set<DocumentSchema>();
    internal static long ByteLength(byte[] value) => throw new NotSupportedException("Database translation only.");
    internal static DateTime ClockUtc() => throw new NotSupportedException("Database translation only.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<ContentObject>(entity =>
        {
            entity.ToTable("objects", table => table.HasCheckConstraint("objects_length", "[Length] >= 0"));
            entity.HasKey(row => row.Key);
            entity.Property(row => row.Key).HasColumnType("char(64)").UseCollation("Latin1_General_100_BIN2");
            entity.Property(row => row.Sha256).HasColumnType("char(64)").UseCollation("Latin1_General_100_BIN2");
        });
        modelBuilder.Entity<ContentChunk>(entity =>
        {
            entity.ToTable("chunks", table =>
            {
                table.HasCheckConstraint("chunks_ordinal", "[Ordinal] >= 0");
                table.HasCheckConstraint("chunks_length", "DATALENGTH([Bytes]) BETWEEN 1 AND 65536");
            });
            entity.HasKey(row => new { row.Key, row.Ordinal });
            entity.Property(row => row.Key).HasColumnType("char(64)").UseCollation("Latin1_General_100_BIN2");
            entity.Property(row => row.Bytes).HasColumnType("varbinary(max)");
            entity.HasOne<ContentObject>().WithMany().HasForeignKey(row => row.Key).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<ContentUsage>(entity =>
        {
            entity.ToTable("usage", table =>
            {
                table.HasCheckConstraint("usage_singleton", "[Id] = 1");
                table.HasCheckConstraint("usage_bytes", "[StoredBytes] >= 0 AND [MaxStoredBytes] > 0");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.DocumentCount).HasDefaultValue(0L);
            entity.Property(row => row.MaxDocuments).HasDefaultValue(10000L);
            entity.ToTable(table => table.HasCheckConstraint("usage_documents", "[DocumentCount] >= 0 AND [MaxDocuments] > 0"));
            entity.HasData(new ContentUsage { Id = 1, MaxStoredBytes = 10L * 1024 * 1024 * 1024, MaxDocuments = 10000 });
        });
        modelBuilder.HasDbFunction(typeof(SqlServerDocumentDbContext).GetMethod(nameof(ByteLength), BindingFlags.Static | BindingFlags.NonPublic)!)
            .HasName("DATALENGTH").IsBuiltIn();
        modelBuilder.HasDbFunction(typeof(SqlServerDocumentDbContext).GetMethod(nameof(ClockUtc), BindingFlags.Static | BindingFlags.NonPublic)!)
            .HasName("SYSUTCDATETIME").IsBuiltIn();
        modelBuilder.Entity<DocumentPointer>(entity =>
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
        modelBuilder.Entity<DocumentSnapshot>(entity =>
        {
            entity.ToTable("snapshots", table => table.HasCheckConstraint("snapshots_version", "[StateVersion] >= 0"));
            entity.HasKey(row => new { row.ResourceId, row.StateVersion });
        });
        modelBuilder.Entity<DocumentMetadata>(entity =>
        {
            entity.ToTable("metadata", table => table.HasCheckConstraint("metadata_revisions", "[RevisionCount] > 0 AND [LifecycleGeneration] > 0"));
            entity.HasKey(row => row.ResourceId);
            entity.Property(row => row.ResourceId).ValueGeneratedNever();
        });
        modelBuilder.Entity<RevisionMetadata>(entity =>
        {
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
        modelBuilder.Entity<RecoveryRecord>(entity =>
        {
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

internal sealed class ContentObject
{
    public required string Key { get; set; }
    public long Length { get; set; }
    public required string Sha256 { get; set; }
}
internal sealed class ContentChunk
{
    public required string Key { get; set; }
    public int Ordinal { get; set; }
    public required byte[] Bytes { get; set; }
}
internal sealed class ContentUsage
{
    public int Id { get; set; }
    public long StoredBytes { get; set; }
    public long MaxStoredBytes { get; set; }
    public long DocumentCount { get; set; }
    public long MaxDocuments { get; set; }
}
