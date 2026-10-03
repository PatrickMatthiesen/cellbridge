using CellBridge.Authentication;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;

if (args.Length == 0) throw new ArgumentException("Commands: create-user, set-user, grant, set-owner, seed-test-user. Pass options as --name value. Operator passwords are read from stdin.");
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
    var user = await users.FindByIdAsync("integration-writer");
    if (user is null)
    {
        user = new() { Id = "integration-writer", UserName = "integration-writer",
            DisplayName = "Integration writer", CanCreate = true };
        Check(await users.CreateAsync(user, password));
    }
    else if (!user.Enabled || !user.CanCreate || user.UserName != "integration-writer" ||
        !await users.CheckPasswordAsync(user, password))
        throw new InvalidOperationException("The existing test account does not match this run. Use a disposable database.");
    Console.WriteLine($"Test subject {user.Subject} is ready.");
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
    var stateStore = new PostgreSqlStateStore(source);
    await stateStore.CheckHealthAsync();
    await stateStore.TransitionAsync(id, (current, now) =>
    {
        if (commandName == "grant" && current.Security.Owner == subject)
            throw new InvalidOperationException("The owner has Read and Write. Transfer ownership before revoking that access.");
        var security = commandName == "set-owner" ? current.Security with { Owner = subject }
            : current.Security with { Grants = access == DocumentAccess.None ? current.Security.Grants.Remove(subject) : current.Security.Grants.SetItem(subject, access) };
        return new StateTransition<bool>(DocumentPermissionUpdates.Apply(current, now, security), true);
    });
    Console.WriteLine($"{commandName} resource={id:D} subject={subject} access={access} actor=operator utc={DateTime.UtcNow:O}");
}
else throw new ArgumentException("Unknown command.");
