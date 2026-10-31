using CellBridge.Authentication;
using CellBridge.Admin;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.Storage.FileSystem;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;

if (args.Length == 0) throw new ArgumentException("Commands: create-user, create-entra-user, link-entra-user, set-user, grant, set-owner, import-directory, seed-test-user. Pass options as --name value. Operator passwords are read from stdin.");
var commandName = args[0];
var values = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 1; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || !args[i].StartsWith("--")) throw new ArgumentException("Pass options as --name value.");
    values.Add(args[i][2..], args[i + 1]);
}
string Required(string key) => values.GetValueOrDefault(key) ?? throw new ArgumentException("Missing --" + key);
var builder = Host.CreateApplicationBuilder();
if (commandName == "seed-test-user" && !builder.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("Test account seeding requires the Testing environment.");
var connectionString = builder.Configuration.GetConnectionString("cellbridge")
    ?? throw new InvalidOperationException("Set ConnectionStrings__cellbridge using operator database credentials.");
await AuthenticationDatabase.CheckSchemaAsync(connectionString);
builder.Services.AddDbContext<AuthenticationDatabase>(o => o.UseNpgsql(connectionString));
builder.Services.AddIdentityCore<CellBridgeUser>(o => o.Password.RequiredLength = 12).AddEntityFrameworkStores<AuthenticationDatabase>();
using var host = builder.Build();
using var scope = host.Services.CreateScope();
var users = scope.ServiceProvider.GetRequiredService<UserManager<CellBridgeUser>>();

void Check(IdentityResult result)
{
    if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
}

if (commandName == "seed-test-user")
{
    // Aspire supplies a secret parameter to this explicitly started test resource.
    // Operator create-user continues to accept its password only through stdin.
    var password = builder.Configuration["Seed:Password"]
        ?? throw new InvalidOperationException("Test account seeding requires Seed:Password.");
    var userCount = builder.Configuration.GetValue("Seed:UserCount", 1);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userCount);
    var template = new CellBridgeUser { UserName = "integration-writer" };
    foreach (var validator in users.PasswordValidators)
        Check(await validator.ValidateAsync(users, template, password));
    // Only this Testing command shares a hash for its common ephemeral password.
    // UserManager still creates independent IDs, security stamps and claims.
    var passwordHash = users.PasswordHasher.HashPassword(template, password);
    for (var userIndex = 0; userIndex < userCount; userIndex++)
    {
        var suffix = userIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var login = userIndex == 0 ? "integration-writer" : "integration-writer-" + suffix;
        var user = await users.FindByIdAsync(login);
        if (user is null)
        {
            user = new() { Id = login, UserName = login, Enabled = true, CanCreate = true,
                DisplayName = userIndex == 0 ? "Integration writer" : "Integration writer " + suffix,
                PasswordHash = passwordHash };
            Check(await users.CreateAsync(user));
        }
        else if (!user.Enabled || !user.CanCreate || user.UserName != login ||
            !await users.CheckPasswordAsync(user, password))
            throw new InvalidOperationException("The existing test account does not match this run. Use a disposable database.");
    }
    Console.WriteLine($"{userCount} test accounts are ready.");
}
else if (commandName == "create-user")
{
    var user = new CellBridgeUser
    {
        UserName = Required("login"), DisplayName = Required("display-name"),
        CanCreate = bool.Parse(values.GetValueOrDefault("can-create", "false")),
    };
    if (values.TryGetValue("id", out var id)) user.Id = id;
    // Password never enters argv, host configuration or logs.
    var password = await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("Supply the password on stdin.");
    Check(await users.CreateAsync(user, password));
    Console.WriteLine($"Created subject {user.Subject}");
}
else if (commandName is "create-entra-user" or "link-entra-user")
{
    var tenantId = Guid.Parse(Required("tenant-id"));
    var objectId = Guid.Parse(Required("object-id"));
    var key = EntraAuthentication.ProviderKey(tenantId, objectId);
    var database = scope.ServiceProvider.GetRequiredService<AuthenticationDatabase>();
    await using var transaction = await database.Database.BeginTransactionAsync();
    // Identity's login key protects an external identity, not the number of
    // identities linked to one local user. Serialize operator link commands.
    await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(748219353)");
    CellBridgeUser user;
    if (commandName == "create-entra-user")
    {
        if (await users.FindByLoginAsync(EntraAuthentication.LoginProvider, key) is not null)
            throw new InvalidOperationException("This Entra identity is already linked.");
        user = new()
        {
            UserName = Required("login"), DisplayName = Required("display-name"),
            CanCreate = bool.Parse(values.GetValueOrDefault("can-create", "false")),
        };
        if (values.TryGetValue("id", out var id)) user.Id = id;
        Check(await users.CreateAsync(user));
    }
    else
    {
        var subject = Required("subject");
        user = subject.StartsWith("local:", StringComparison.Ordinal)
            ? await users.FindByIdAsync(subject[6..]) ?? throw new KeyNotFoundException("Unknown local user.")
            : throw new ArgumentException("Supply the local subject to link.");
        if (!user.Enabled) throw new InvalidOperationException("The account is disabled.");
    }
    var existing = await users.FindByLoginAsync(EntraAuthentication.LoginProvider, key);
    if (existing is not null && existing.Id != user.Id)
        throw new InvalidOperationException("This Entra identity belongs to a different local account.");
    var logins = await users.GetLoginsAsync(user);
    if (logins.Any(login => login.LoginProvider == EntraAuthentication.LoginProvider && login.ProviderKey != key))
        throw new InvalidOperationException("The local account already has a different Entra identity.");
    if (existing is null)
        Check(await users.AddLoginAsync(user, new(EntraAuthentication.LoginProvider, key, "Microsoft Entra")));
    Check(await users.UpdateSecurityStampAsync(user));
    await transaction.CommitAsync();
    Console.WriteLine($"Entra identity linked to subject {user.Subject}.");
}
else if (commandName == "set-user")
{
    var user = await users.FindByIdAsync(Required("subject").Replace("local:", "", StringComparison.Ordinal))
        ?? throw new KeyNotFoundException("Unknown user.");
    if (values.TryGetValue("enabled", out var enabled)) user.Enabled = bool.Parse(enabled);
    if (values.TryGetValue("can-create", out var canCreate)) user.CanCreate = bool.Parse(canCreate);
    if (values.TryGetValue("display-name", out var displayName)) user.DisplayName = displayName;
    Check(await users.UpdateSecurityStampAsync(user));
    Console.WriteLine($"Updated subject {user.Subject}; existing cookies expire at their next security-stamp check.");
}
else if (commandName == "import-directory")
{
    var subject = Required("owner");
    var user = subject.StartsWith("local:", StringComparison.Ordinal)
        ? await users.FindByIdAsync(subject[6..]) : null;
    if (user is null || !user.Enabled) throw new ArgumentException("Supply --owner with the subject of an enabled, provisioned local user.");
    var directory = Path.GetFullPath(Required("directory"));
    var limits = builder.Configuration.GetSection("Storage").Get<StorageLimits>() ?? new StorageLimits();
    limits.Validate();
    await using var source = NpgsqlDataSource.Create(connectionString);
    IContentStore content = builder.Configuration["Storage:ContentProvider"]?.ToLowerInvariant() switch
    {
        null or "postgresql" => new PostgreSqlContentStore(source, limits.MaxObjectBytes),
        "filesystem" => new PostgreSqlFileSystemContentStore(source, builder.Configuration["Storage:ContentRoot"]
            ?? throw new InvalidOperationException("Filesystem content requires Storage:ContentRoot."), limits.MaxObjectBytes,
            builder.Configuration.GetValue("Storage:SharedContent", false)),
        _ => throw new InvalidOperationException("Unknown binary content provider."),
    };
    var provider = new StorageProvider(new PostgreSqlStateStore(source, limits), content, limits);
    await provider.CheckHealthAsync();
    var result = await DocumentImports.ImportDirectoryAsync(new CellBridgeDocumentService(provider), directory,
        new SubjectIdentity(user.Subject, user.UserName!, user.DisplayName));
    Console.WriteLine($"Imported {result.Imported} documents; preserved {result.Skipped} existing paths. owner={subject}");
}
else if (commandName is "grant" or "set-owner")
{
    var subject = Required("subject");
    if (!subject.StartsWith("local:", StringComparison.Ordinal) || await users.FindByIdAsync(subject[6..]) is null)
        throw new ArgumentException("Supply the subject of a provisioned local user.");
    var id = Guid.Parse(Required("resource-id"));
    var access = commandName == "grant" ? Required("access") switch
    {
        "none" => DocumentAccess.None, "read" => DocumentAccess.Read,
        "write" => DocumentAccess.Read | DocumentAccess.Write, _ => throw new ArgumentException("Access must be none, read or write."),
    } : DocumentAccess.None;
    await using var source = NpgsqlDataSource.Create(connectionString);
    IDocumentRepository stateStore = new PostgreSqlStateStore(source);
    await stateStore.CheckHealthAsync();
    var current = await stateStore.ReadControlAsync(id) ?? throw new KeyNotFoundException("The document does not exist.");
    if (commandName == "grant" && current.Head.Security.Owner == subject)
        throw new InvalidOperationException("The owner has Read and Write. Transfer ownership before revoking that access.");
    var security = commandName == "set-owner" ? current.Head.Security with { Owner = subject }
        : current.Head.Security with { Grants = access == DocumentAccess.None ? current.Head.Security.Grants.Remove(subject)
            : current.Head.Security.Grants.SetItem(subject, access) };
    // This command runs as the trusted database operator, outside the user-facing protocol endpoints.
    var admission = new DocumentActorAdmission(new("system:operator", "operator", "Operator"), id,
        current.Head.LifecycleGeneration, current.Head.Security.AuthorizationPolicy, DocumentAccess.Read | DocumentAccess.Write)
    { AuthorityGeneration = current.Head.AuthorityGeneration, StateVersion = current.Head.StateVersion };
    var result = await stateStore.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(id, admission, security, current.Head.AuthorityGeneration));
    if (result.ErrorCode is { } error) throw new InvalidOperationException($"The permission update failed: {error}.");
    Console.WriteLine($"{commandName} resource={id:D} subject={subject} access={access} actor=operator utc={DateTime.UtcNow:O}");
}
else throw new ArgumentException("Unknown command.");
