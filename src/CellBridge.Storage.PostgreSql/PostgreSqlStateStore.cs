using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.PostgreSql;

/// <summary>Normalized document persistence with affected-document coordination and coherent reads.</summary>
public sealed partial class PostgreSqlStateStore
{
    private readonly DbContextOptions<CellBridgeDbContext> options;
    private readonly StorageLimits? limits;
    private readonly IContentStore? validationContent;

    public PostgreSqlStateStore(Npgsql.NpgsqlDataSource source, StorageLimits? limits = null,
        IContentStore? validationContent = null)
        : this(PostgreSqlDatabase.CreateOptions(source), limits, validationContent) { }

    public PostgreSqlStateStore(DbContextOptions<CellBridgeDbContext> options, StorageLimits? limits = null,
        IContentStore? validationContent = null)
    {
        this.options = PostgreSqlDatabase.NormalizeOptions(options);
        this.limits = limits;
        this.validationContent = validationContent;
        repository = new(new PostgreSqlRepositoryDatabase(this.options), limits, validationContent);
    }

    public bool Durable => repository.Durable;
    public bool Shared => repository.Shared;
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgreSqlStorageMigrator(options, limits, validationContent).MigrateAsync(cancellationToken);
    internal static Task ConfigureTransactionAsync(CellBridgeDbContext context, CancellationToken cancellationToken) =>
        PostgreSqlDocumentDatabase.ConfigureTransactionAsync(context, cancellationToken);
}
