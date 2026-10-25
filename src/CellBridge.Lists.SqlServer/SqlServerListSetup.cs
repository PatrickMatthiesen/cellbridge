using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

/// <summary>Explicit administrator registration, separate from the restricted runtime engine.</summary>
public static class SqlServerListSetup
{
    public static Task RegisterWorkspaceAsync(DbContextOptions<SqlServerListDbContext> options, Guid workspaceId,
        string name, string administratorSubject, CancellationToken ct = default) =>
        EfListEngine.RegisterWorkspaceAsync(options, workspaceId, name, administratorSubject, ct);

    public static Task RegisterHostWorkspaceAsync(DbContextOptions<SqlServerListDbContext> options, Guid workspaceId,
        string name, string policyDomain, string authorizationScope, CancellationToken ct = default) =>
        EfListEngine.RegisterHostWorkspaceAsync(options, workspaceId, name, policyDomain, authorizationScope, ct);

    public static Task RegisterTemplateAsync(DbContextOptions<SqlServerListDbContext> options,
        ListTemplate template, CancellationToken ct = default) => EfListEngine.RegisterTemplateAsync(options, template, ct);

    internal static byte[] SubjectKey(string subject) => SHA256.HashData(Encoding.Unicode.GetBytes(subject));
}
