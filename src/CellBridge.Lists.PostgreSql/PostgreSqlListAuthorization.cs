using Npgsql;

namespace CellBridge.Lists.PostgreSql;

public sealed partial class PostgreSqlListEngine
{
    private readonly IListAuthorizationProvider? _authorizationProvider;
    private readonly string? _authorizationDomain;

    /// <summary>
    /// Use one shared host authority provider for every engine and permission writer in the process.
    /// Host-bound workspaces never consult the built-in membership or grant tables.
    /// </summary>
    public PostgreSqlListEngine(NpgsqlDataSource runtimeSource, ListLimits? limits,
        IListAuthorizationProvider authorizationProvider) : this(runtimeSource, limits)
    {
        _authorizationProvider = authorizationProvider ?? throw new ArgumentNullException(nameof(authorizationProvider));
        var domain = authorizationProvider.PolicyDomain;
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 200 || !ListValidation.IsWellFormedText(domain) || domain.Any(char.IsControl))
            throw new ArgumentException("A valid host authorization domain is required.", nameof(authorizationProvider));
        _authorizationDomain = domain;
    }

    /// <summary>
    /// Coordinate a host permission transaction affecting this workspace. The callback owns its entire
    /// authority-store transaction and must settle before returning. It must not start detached writes,
    /// reenter list operations, or acquire another authorization guard. Permission writers must use this
    /// helper, not merely the provider's write lease. Cancellation never releases an unsettled callback.
    /// </summary>
    public Task<T> CoordinateAuthorizationChangeAsync<T>(Guid workspaceId,
        Func<CancellationToken, Task<T>> commitHostChange, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty) throw Validation("A workspace ID is required.");
        return CoordinateHostChangeAsync(workspaceId, commitHostChange, ct);
    }

    /// <summary>
    /// Coordinate authority changes such as account disablement or global roles affecting any workspace
    /// in this policy domain. The callback has the same settlement and no-reentry contract as the
    /// workspace helper. This does not coordinate document authorization or other modules.
    /// </summary>
    public Task<T> CoordinateGlobalAuthorizationChangeAsync<T>(
        Func<CancellationToken, Task<T>> commitHostChange, CancellationToken ct = default) =>
        CoordinateHostChangeAsync<T>(null, commitHostChange, ct);

    private async Task<T> CoordinateHostChangeAsync<T>(Guid? workspaceId,
        Func<CancellationToken, Task<T>> commitHostChange, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commitHostChange);
        if (_authorizationProvider is null) throw HostAuthorityUnsupported();
        await EnsureHealthyAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_limits.StatementTimeout);
        IAsyncDisposable acquired;
        try { acquired = await AcquireHostWriteAsync(workspaceId, deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw HostAuthorityUnavailable(); }
        await using var guard = acquired;
        // A prior client can have lost the outcome of COMMIT while its PostgreSQL transaction
        // still holds its shared lock. Drain those transactions before changing host authority.
        // No host-policy I/O or permission mutation executes inside this PostgreSQL transaction.
        try
        {
            await using (var connection = await _source.OpenConnectionAsync(deadline.Token))
            await using (var transaction = await connection.BeginTransactionAsync(deadline.Token))
            {
                await ConfigureHostTimeoutsAsync(connection, transaction, deadline.Token);
                await LockHostDomainAsync(connection, transaction, workspaceId is null, deadline.Token);
                if (workspaceId is { } id)
                {
                    await PostgreSqlListSetup.SetContextAsync(connection, transaction, id, deadline.Token);
                    await ExecuteAsync(connection, transaction,
                        "SELECT pg_advisory_xact_lock(hashtextextended($1,391813))", deadline.Token, id.ToString("D"));
                    await RequireHostWorkspaceAsync(connection, transaction, id, deadline.Token);
                }
                await transaction.CommitAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw HostAuthorityUnavailable(); }
        catch (NpgsqlException) { throw HostAuthorityUnavailable(); }
        deadline.Token.ThrowIfCancellationRequested();
        // Await the callback itself, not WaitAsync(deadline). Even an uncooperative callback
        // retains exclusion until it actually commits or rolls back and settles.
        return await commitHostChange(deadline.Token);
    }

    private async ValueTask<IListAuthorizationReadLease> AcquireHostReadAsync(ListActor actor,
        Guid workspaceId, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_limits.StatementTimeout);
        IListAuthorizationReadLease? lease = null;
        try
        {
            lease = await _authorizationProvider!.AcquireReadAsync(actor, workspaceId, deadline.Token);
            var snapshot = lease?.Snapshot;
            if (snapshot is null || snapshot.WorkspaceId != workspaceId ||
                !string.Equals(snapshot.Subject, actor.Subject, StringComparison.Ordinal) ||
                !string.Equals(snapshot.PolicyDomain, _authorizationDomain, StringComparison.Ordinal))
                throw HostAuthorityUnavailable();
            deadline.Token.ThrowIfCancellationRequested();
            return lease!;
        }
        catch (Exception ex)
        {
            if (lease is not null) await lease.DisposeAsync();
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw HostAuthorityUnavailable();
        }
    }

    private async ValueTask<IAsyncDisposable> AcquireHostWriteAsync(Guid? workspaceId, CancellationToken ct)
    {
        try
        {
            return (workspaceId is { } id
                ? await _authorizationProvider!.AcquireWriteAsync(id, ct)
                : await _authorizationProvider!.AcquireGlobalWriteAsync(ct)) ?? throw HostAuthorityUnavailable();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw HostAuthorityUnavailable(); }
    }

    private async Task RequireHostWorkspaceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, CancellationToken ct)
    {
        var domain = await ScalarAsync<string?>(connection, transaction,
            "SELECT authorization_domain FROM cellbridge_lists.workspaces WHERE workspace_id=$1", ct, workspaceId);
        if (!string.Equals(domain, _authorizationDomain, StringComparison.Ordinal) || domain is null)
            throw HostAuthorityUnavailable();
    }

    private Task ConfigureHostTimeoutsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        var milliseconds = Math.Clamp((long)_limits.StatementTimeout.TotalMilliseconds, 1, int.MaxValue);
        return ExecuteAsync(connection, transaction,
            $"SET LOCAL statement_timeout={milliseconds}; SET LOCAL lock_timeout={milliseconds}", ct);
    }

    // PostgreSQL keeps its two-int and bigint advisory-lock key spaces disjoint. Domain locks use
    // (391814, hashtext(domain)); workspace locks retain the existing bigint key/seed 391813.
    // Hash collisions within a level can only add exclusion. Keys do not depend on .NET hash codes.
    private Task LockHostDomainAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        bool exclusive, CancellationToken ct) => ExecuteAsync(connection, transaction,
            exclusive ? "SELECT pg_advisory_xact_lock(391814,hashtext($1))"
                : "SELECT pg_advisory_xact_lock_shared(391814,hashtext($1))", ct, _authorizationDomain);

    private static ListException HostAuthorityUnavailable() =>
        new(ListFailure.Unavailable, "The host list authority is unavailable or does not match this workspace.");

    private static ListException HostAuthorityUnsupported() =>
        new(ListFailure.NotSupported, "Permission changes belong to the configured host authority.");
}
