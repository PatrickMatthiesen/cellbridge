using System.Text.Json;
using CellBridge.Lists;
using Microsoft.Data.Sqlite;

namespace CellBridge.Examples.HostAuthorizedLists;

/// <summary>A small host-owned account and list-permission store for the source example.</summary>
public sealed class SqliteListAuthority : IAsyncDisposable
{
    private const ListWorkspaceAccess AllWorkspaceAccess = ListWorkspaceAccess.UseLists |
        ListWorkspaceAccess.CreateLists | ListWorkspaceAccess.ManagePermissions | ListWorkspaceAccess.ReadAudit;
    private const ListAccess AllListAccess = ListAccess.Read | ListAccess.Write | ListAccess.Design;

    private readonly string _connectionString;
    private int _disposed;

    public SqliteListAuthority(string databasePath, string policyDomain)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A SQLite database path is required.", nameof(databasePath));
        if (string.IsNullOrWhiteSpace(policyDomain) || policyDomain.Length > 200 ||
            !ListValidation.IsWellFormedText(policyDomain) || policyDomain.Any(char.IsControl))
            throw new ArgumentException("A valid policy domain is required.", nameof(policyDomain));

        PolicyDomain = policyDomain;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true
        }.ConnectionString;
    }

    public string PolicyDomain { get; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS host_accounts (
                subject TEXT PRIMARY KEY,
                enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                revision INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS host_workspace_permissions (
                subject TEXT NOT NULL REFERENCES host_accounts(subject) ON DELETE CASCADE,
                workspace_id TEXT NOT NULL,
                workspace_access INTEGER NOT NULL,
                default_list_access INTEGER NOT NULL,
                list_overrides_json TEXT NOT NULL CHECK (json_valid(list_overrides_json)),
                revision INTEGER NOT NULL,
                PRIMARY KEY (subject, workspace_id)
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Loads one subject and workspace. Pass this method to the singleton authorization provider.
    /// The provider calls it while holding its read guard and before the list engine starts PostgreSQL work.
    /// </summary>
    public async ValueTask<ListAuthorizationSnapshot?> LoadSnapshotAsync(
        ListActor actor, Guid workspaceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ThrowIfDisposed();
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.enabled, a.revision, p.workspace_access, p.default_list_access,
                   p.list_overrides_json, p.revision
            FROM host_accounts a
            LEFT JOIN host_workspace_permissions p
              ON p.subject = a.subject AND p.workspace_id = $workspace
            WHERE a.subject = $subject
            """;
        command.Parameters.AddWithValue("$subject", actor.Subject);
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var accountRevision = reader.GetInt64(1);
        if (!reader.GetBoolean(0) || reader.IsDBNull(2))
            return new ListAuthorizationSnapshot(PolicyDomain, actor.Subject, workspaceId,
                ListWorkspaceAccess.None, revision: $"{accountRevision}:none");

        var workspaceAccess = (ListWorkspaceAccess)reader.GetInt32(2);
        var defaultListAccess = (ListAccess)reader.GetInt32(3);
        var overrides = DeserializeOverrides(reader.GetString(4));
        var permissionRevision = reader.GetInt64(5);
        return new ListAuthorizationSnapshot(PolicyDomain, actor.Subject, workspaceId,
            workspaceAccess, defaultListAccess, overrides, $"{accountRevision}:{permissionRevision}");
    }

    /// <summary>
    /// Runs one complete SQLite permission transaction. The callback must finish all work before it returns.
    /// It must not detach writes or call list operations.
    /// </summary>
    public async Task<T> InTransactionAsync<T>(
        Func<PermissionTransaction, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var writer = new PermissionTransaction(connection, (SqliteTransaction)transaction);
        try
        {
            var result = await operation(writer, ct);
            await transaction.CommitAsync(CancellationToken.None);
            return result;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // A failed COMMIT can leave no active transaction to roll back.
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static IReadOnlyDictionary<Guid, ListAccess> DeserializeOverrides(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, int>>(json)
            ?? throw new InvalidDataException("The list override JSON must be an object.");
        var result = new Dictionary<Guid, ListAccess>(values.Count);
        foreach (var (key, value) in values)
        {
            if (!Guid.TryParseExact(key, "D", out var listId))
                throw new InvalidDataException("A list override key is not a canonical GUID.");
            result.Add(listId, (ListAccess)value);
        }
        return result;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0, this);

    public sealed class PermissionTransaction
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;

        internal PermissionTransaction(SqliteConnection connection, SqliteTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public async Task SetAccountEnabledAsync(string subject, bool enabled, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("A subject is required.", nameof(subject));
            await using var command = CreateCommand("""
                INSERT INTO host_accounts(subject, enabled, revision) VALUES ($subject, $enabled, 1)
                ON CONFLICT(subject) DO UPDATE SET
                    enabled = excluded.enabled,
                    revision = host_accounts.revision + 1
                """);
            command.Parameters.AddWithValue("$subject", subject);
            command.Parameters.AddWithValue("$enabled", enabled);
            await command.ExecuteNonQueryAsync(ct);
        }

        public async Task SetWorkspacePermissionAsync(
            string subject,
            Guid workspaceId,
            ListWorkspaceAccess workspaceAccess,
            ListAccess defaultListAccess,
            IReadOnlyDictionary<Guid, ListAccess>? listOverrides = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("A subject is required.", nameof(subject));
            if (workspaceId == Guid.Empty)
                throw new ArgumentException("A workspace ID is required.", nameof(workspaceId));
            if ((workspaceAccess & ~AllWorkspaceAccess) != 0 ||
                workspaceAccess != ListWorkspaceAccess.None && !workspaceAccess.HasFlag(ListWorkspaceAccess.UseLists))
                throw new ArgumentOutOfRangeException(nameof(workspaceAccess));
            if ((defaultListAccess & ~AllListAccess) != 0)
                throw new ArgumentOutOfRangeException(nameof(defaultListAccess));

            var serializedOverrides = new Dictionary<string, int>();
            foreach (var (listId, access) in listOverrides ?? new Dictionary<Guid, ListAccess>())
            {
                if (listId == Guid.Empty || (access & ~AllListAccess) != 0)
                    throw new ArgumentException("A list override is invalid.", nameof(listOverrides));
                serializedOverrides.Add(listId.ToString("D"), (int)access);
            }

            await using var command = CreateCommand("""
                INSERT INTO host_workspace_permissions(
                    subject, workspace_id, workspace_access, default_list_access,
                    list_overrides_json, revision)
                VALUES ($subject, $workspace, $workspace_access, $default_list_access, $overrides, 1)
                ON CONFLICT(subject, workspace_id) DO UPDATE SET
                    workspace_access = excluded.workspace_access,
                    default_list_access = excluded.default_list_access,
                    list_overrides_json = excluded.list_overrides_json,
                    revision = host_workspace_permissions.revision + 1
                """);
            command.Parameters.AddWithValue("$subject", subject);
            command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
            command.Parameters.AddWithValue("$workspace_access", (int)workspaceAccess);
            command.Parameters.AddWithValue("$default_list_access", (int)defaultListAccess);
            command.Parameters.AddWithValue("$overrides", JsonSerializer.Serialize(serializedOverrides));
            await command.ExecuteNonQueryAsync(ct);
        }

        private SqliteCommand CreateCommand(string text)
        {
            var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = text;
            return command;
        }
    }
}
