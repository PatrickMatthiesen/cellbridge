using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

/// <summary>Library-owned SQL Server content model, configured by the consumer.</summary>
public sealed class SqlServerDocumentDbContext(DbContextOptions<SqlServerDocumentDbContext> options) : DbContext(options)
{
    public const string Schema = "cellbridge_documents";
    public const string MigrationsHistoryTable = "__CellBridgeDocumentMigrationsHistory";
    internal DbSet<ContentObject> Objects => Set<ContentObject>();
    internal DbSet<ContentChunk> Chunks => Set<ContentChunk>();
    internal DbSet<ContentUsage> Usage => Set<ContentUsage>();

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
            entity.HasData(new ContentUsage { Id = 1, MaxStoredBytes = 10L * 1024 * 1024 * 1024 });
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
}
