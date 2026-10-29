using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql.Migrations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

/// <summary>Applies library-owned storage migrations when the consumer explicitly invokes it.</summary>
public sealed class PostgreSqlStorageMigrator
{
    private readonly DbContextOptions<CellBridgeDbContext> _options;
    private readonly StorageLimits _limits;
    private readonly IContentStore? migrationContent;

    public PostgreSqlStorageMigrator(NpgsqlDataSource source, StorageLimits? limits = null, IContentStore? migrationContent = null)
        : this(PostgreSqlDatabase.CreateOptions(source), limits, migrationContent) { }

    public PostgreSqlStorageMigrator(DbContextOptions<CellBridgeDbContext> options, StorageLimits? limits = null, IContentStore? migrationContent = null)
    {
        _options = PostgreSqlDatabase.NormalizeOptions(options);
        this.migrationContent = migrationContent;
        _limits = limits ?? new StorageLimits();
        _limits.Validate();
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = new CellBridgeDbContext(_options);
        await context.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var migrationLock = new NpgsqlCommand("SELECT pg_advisory_lock(748219351)", connection);
        await migrationLock.ExecuteNonQueryAsync(cancellationToken);
        try
        {
            if (await HasLegacySchemaAsync(connection, cancellationToken))
                await ImportLegacySchemaAsync(context, cancellationToken);
            else
                await CreateFreshSchemaAsync(context, cancellationToken);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(748219351)", connection);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task CreateFreshSchemaAsync(CellBridgeDbContext context, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await ConfigureMigrationTransactionAsync(context, cancellationToken);
        var (connection, npgsqlTransaction) = PostgreSqlDatabase.GetTransaction(context);
        await CreateHistoryTableAsync(connection, npgsqlTransaction, cancellationToken);
        await ValidateHistoryAsync(connection, npgsqlTransaction, false, cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
        await PostgreSqlStateMigration.ValidateRecoverySchemaAsync(
            connection, npgsqlTransaction, cancellationToken);
        await new RelationalDocumentRepository<CellBridgeDbContext>(new PostgreSqlRepositoryDatabase(_options), _limits)
            .BackfillAsync(context, migrationContent, cancellationToken);
        await PostgreSqlStateStore.ConfigureTransactionAsync(context, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> HasLegacySchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass('cellbridge_schema') IS NOT NULL", connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task ImportLegacySchemaAsync(CellBridgeDbContext context, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var (connection, npgsqlTransaction) = PostgreSqlDatabase.GetTransaction(context);
        await ConfigureMigrationTransactionAsync(context, cancellationToken);
        await using (var schemaGate = new NpgsqlCommand(
            "LOCK TABLE cellbridge_schema IN ACCESS EXCLUSIVE MODE", connection, npgsqlTransaction))
            await schemaGate.ExecuteNonQueryAsync(cancellationToken);

        var versions = await context.SchemaVersions.AsNoTracking().Select(row => row.Version).ToListAsync(cancellationToken);
        if (versions.Count != 1)
            throw new StorageUnavailableException(versions.Count == 0
                ? "Missing storage schema version."
                : "Multiple storage schema versions.");
        var previousSchema = versions[0];
        if (previousSchema is not (3 or 4 or 5 or 6 or 7 or 8 or 9))
            throw new StorageUnavailableException(
                "Unsupported storage schema. Only schema 3 through 9 initialization is supported; persisted data was not changed.");

        await CreateHistoryTableAsync(connection, npgsqlTransaction, cancellationToken);
        await ValidateHistoryAsync(connection, npgsqlTransaction, previousSchema >= 6, cancellationToken,
            allowAllowances: previousSchema >= 7, allowRemoval: previousSchema >= 8, allowNormalized: previousSchema >= 9);

        if (previousSchema <= 5)
        {
            await using var source = typeof(PostgreSqlStateStore).Assembly
                .GetManifestResourceStream("CellBridge.Storage.PostgreSql.Schema.sql")!;
            using var reader = new StreamReader(source);
            await using (var schema = new NpgsqlCommand(await reader.ReadToEndAsync(cancellationToken), connection, npgsqlTransaction))
                await schema.ExecuteNonQueryAsync(cancellationToken);
        }
        await PostgreSqlStateMigration.InitializeRecoveryAsync(connection, npgsqlTransaction, cancellationToken);
        if (previousSchema is 3 or 4)
            await PostgreSqlStateMigration.MigrateGraphMetadataAsync(connection, npgsqlTransaction, _limits, cancellationToken);
        if (previousSchema is 3 or 4 or 5)
            await PostgreSqlStateMigration.MigrateSelectiveReadsAsync(connection, npgsqlTransaction, _limits, cancellationToken);

        await CreateHistoryTableAsync(connection, npgsqlTransaction, cancellationToken);
        await using (var import = new NpgsqlCommand($"""
            INSERT INTO "{CellBridgeDbContext.MigrationsHistoryTable}" ("MigrationId","ProductVersion") VALUES ($1,$2)
                ON CONFLICT ("MigrationId") DO NOTHING
            """, connection, npgsqlTransaction))
        {
            import.Parameters.AddWithValue(BaselineSchema6.MigrationId);
            import.Parameters.AddWithValue("10.0.4");
            await import.ExecuteNonQueryAsync(cancellationToken);
        }
        await context.Database.MigrateAsync(cancellationToken);
        await new RelationalDocumentRepository<CellBridgeDbContext>(new PostgreSqlRepositoryDatabase(_options), _limits)
            .BackfillAsync(context, migrationContent, cancellationToken);
        await PostgreSqlStateStore.ConfigureTransactionAsync(context, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static Task ConfigureMigrationTransactionAsync(CellBridgeDbContext context,
        CancellationToken cancellationToken) => context.Database.ExecuteSqlRawAsync(
            "SET LOCAL synchronous_commit=on; SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s'",
            cancellationToken);

    private static async Task ValidateHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        bool allowBaseline, CancellationToken cancellationToken, bool allowAllowances = false, bool allowRemoval = false, bool allowNormalized = false)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT \"MigrationId\" FROM \"{CellBridgeDbContext.MigrationsHistoryTable}\"", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var applied = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var migration = reader.GetString(0);
            applied.Add(migration);
            if (!(allowBaseline && migration == BaselineSchema6.MigrationId) &&
                !(allowAllowances && migration == UsageAllowanceMigration.MigrationId) &&
                !(allowRemoval && migration == RemoveSharedQuotas.MigrationId) &&
                !(allowNormalized && migration == NormalizedDocumentRepository.MigrationId))
                throw new StorageUnavailableException("Unsupported or inconsistent document migration history; persisted data was not changed.");
        }
        if (allowNormalized && !applied.Contains(NormalizedDocumentRepository.MigrationId))
            throw new StorageUnavailableException("The normalized document schema is missing its migration history.");
        if (allowRemoval && !applied.Contains(RemoveSharedQuotas.MigrationId))
            throw new StorageUnavailableException("The storage schema is missing its quota-removal migration history.");
        if (allowAllowances && (!applied.Contains(BaselineSchema6.MigrationId) || !applied.Contains(UsageAllowanceMigration.MigrationId)))
            throw new StorageUnavailableException("The storage allowance schema is missing its migration history.");
    }

    private static async Task CreateHistoryTableAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            CREATE TABLE IF NOT EXISTS "{CellBridgeDbContext.MigrationsHistoryTable}" (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK_{CellBridgeDbContext.MigrationsHistoryTable}" PRIMARY KEY ("MigrationId")
            )
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

}
