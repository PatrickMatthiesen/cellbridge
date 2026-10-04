using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Authentication;

public sealed class CellBridgeUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public bool CanCreate { get; set; }
    public bool Enabled { get; set; } = true;
    public string Subject => "local:" + Id;
}

public sealed class AuthenticationKey
{
    public long Id { get; set; }
    public string Xml { get; set; } = "";
}

public sealed class AuthenticationDatabase(DbContextOptions<AuthenticationDatabase> options)
    : IdentityDbContext<CellBridgeUser>(options)
{
    public DbSet<AuthenticationKey> Keys => Set<AuthenticationKey>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<AuthenticationKey>().ToTable("cellbridge_auth_keys");
    }

    public static async Task InitializeAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var durability = new NpgsqlCommand("SET LOCAL synchronous_commit=on", connection, transaction))
            await durability.ExecuteNonQueryAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(748219352)", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        await using var exists = new NpgsqlCommand("SELECT to_regclass('cellbridge_auth_schema')::text", connection, transaction);
        if (await exists.ExecuteScalarAsync(cancellationToken) is not string)
        {
            var options = new DbContextOptionsBuilder<AuthenticationDatabase>().UseNpgsql(connectionString).Options;
            await using var context = new AuthenticationDatabase(options);
            // Explicit schema initialization. EnsureCreated would skip Identity in
            // a database that already contains the document tables.
            await using var schema = new NpgsqlCommand(context.Database.GenerateCreateScript() +
                "CREATE TABLE cellbridge_auth_schema(version integer PRIMARY KEY); INSERT INTO cellbridge_auth_schema VALUES (1);",
                connection, transaction);
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        await CheckSchemaAsync(connectionString, cancellationToken);
    }

    public static async Task CheckSchemaAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT version FROM cellbridge_auth_schema", connection);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        if (!await rows.ReadAsync(cancellationToken) || rows.GetInt32(0) != 1 || await rows.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Run CellBridge.Storage.Setup before starting the host.");
    }
}

internal sealed class DatabaseKeyRepository(IDbContextFactory<AuthenticationDatabase> factory) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var context = factory.CreateDbContext();
        return context.Keys.AsNoTracking().OrderBy(k => k.Id).Select(k => k.Xml).AsEnumerable().Select(XElement.Parse).ToArray();
    }
    public void StoreElement(XElement element, string friendlyName)
    {
        using var context = factory.CreateDbContext();
        context.Keys.Add(new() { Xml = element.ToString(SaveOptions.DisableFormatting) });
        context.SaveChanges();
    }
}
