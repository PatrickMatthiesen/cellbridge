using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

/// <summary>Normalized document persistence with affected-document coordination and coherent reads.</summary>
public sealed partial class SqlServerStateStore
{
    public SqlServerStateStore(DbContextOptions<SqlServerDocumentDbContext> options, StorageLimits? limits = null,
        IContentStore? validationContent = null)
    {
        options = SqlServerDocumentDatabase.Normalize(options);
        repository = new(new SqlServerRepositoryDatabase(options), limits, validationContent);
    }
    public bool Durable => repository.Durable;
    public bool Shared => repository.Shared;
}
