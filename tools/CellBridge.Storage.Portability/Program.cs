using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Microsoft.Extensions.Configuration;
using Npgsql;

if (args.Length == 0)
    throw new ArgumentException("Commands: export, validate, import, migrate, restore-version. See docs/provider-portability.md. Options use --name value.");
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 1; i < args.Length; i += 2)
{
    if (i + 1 == args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException("Options use --name value.");
    options.Add(args[i][2..], args[i + 1]);
}
string Required(string key) => options.GetValueOrDefault(key) ?? throw new ArgumentException("Missing --" + key);
var allowed = args[0] switch
{
    "export" or "import" or "validate" or "migrate" => new[] { "archive", "context" },
    "restore-version" => new[] { "resource-id", "revision", "expected-revision", "operation-key", "subject" },
    _ => throw new ArgumentException("Unknown portability command."),
};
if (options.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException("Unknown command option.");
var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var sources = new List<NpgsqlDataSource>();
StorageProvider Provider(string name)
{
    var section = configuration.GetSection(name == "cellbridge" ? "Storage" : name + ":Storage");
    var limits = section.Get<StorageLimits>() ?? new();
    var connection = configuration.GetConnectionString(name) ?? throw new InvalidOperationException("Missing connection string for " + name);
    var source = NpgsqlDataSource.Create(connection); sources.Add(source);
    IContentStore content = section["ContentProvider"]?.ToLowerInvariant() switch
    {
        null or "postgresql" => new PostgreSqlContentStore(source, limits.MaxObjectBytes),
        "filesystem" => new PostgreSqlFileSystemContentStore(source, section["ContentRoot"] ??
            throw new ArgumentException("Filesystem recovery requires ContentRoot."), limits.MaxObjectBytes, section.GetValue("SharedContent", false)),
        _ => throw new NotSupportedException("Unknown recovery content provider."),
    };
    return new(new PostgreSqlStateStore(source, limits), content, limits);
}
try
{
    object result;
    if (args[0] == "restore-version")
    {
        var provider = Provider("cellbridge");
        await provider.State.CheckHealthAsync();
        var subject = Required("subject");
        // Trusted operator identifies the actor; the regular restore API still enforces stored Write access.
        var restored = await new CellBridgeDocumentService(provider).RestoreRevisionAsync(Guid.Parse(Required("resource-id")),
            ulong.Parse(Required("revision")), ulong.Parse(Required("expected-revision")), Required("operation-key"),
            new(new SubjectIdentity(subject, subject, subject)));
        result = new { restored.Revision.ResourceId, restored.Revision.RevisionNumber, restored.IsReplay };
    }
    else
    {
        var context = RecoveryJson.Read<RecoveryContext>(await File.ReadAllBytesAsync(Required("context")));
        var archive = Required("archive");
        var limits = configuration.GetSection("Storage").Get<StorageLimits>() ?? new();
        var portable = new PortableArchive(limits, configuration.GetSection("Recovery").Get<PortableRecoveryLimits>());
        result = args[0] switch
        {
            "export" => await portable.ExportAsync(Provider("cellbridge"), archive, context),
            "validate" => await portable.ValidateAsync(archive, context),
            "import" => await portable.ImportAsync(Provider("cellbridge"), archive, context),
            "migrate" => await portable.MigrateAsync(Provider("source"), Provider("destination"), archive, context),
            _ => throw new ArgumentException("Unknown command."),
        };
    }
    // Print only public IDs, counts and digests. Never connection strings, permission records or context secrets.
    Console.WriteLine(JsonSerializer.Serialize(result));
}
finally { foreach (var source in sources) await source.DisposeAsync(); }
