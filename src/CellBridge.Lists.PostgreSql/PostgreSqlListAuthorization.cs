using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace CellBridge.Lists.PostgreSql;

public sealed partial class PostgreSqlListEngine
{
    private readonly IListAuthorizationProvider? _authorizationProvider;
    private readonly string? _authorizationDomain;
    private readonly byte[] _continuationKey = RandomNumberGenerator.GetBytes(32);
    private const int MaximumContinuationLength = 512;
    // Private encoding may change across releases; default keys are ephemeral per engine.
    private const string ContinuationPurpose = "CellBridge.Lists.Page.v3";

    public PostgreSqlListEngine(NpgsqlDataSource runtimeSource, ListLimits? limits,
        IListAuthorizationProvider authorizationProvider, byte[]? continuationKey = null)
        : this(runtimeSource, limits)
    {
        _authorizationProvider = authorizationProvider ?? throw new ArgumentNullException(nameof(authorizationProvider));
        _authorizationDomain = ValidateDomain(authorizationProvider.PolicyDomain);
        if (continuationKey is not null)
        {
            if (continuationKey.Length != 32) throw new ArgumentException("The continuation key must contain 32 bytes.", nameof(continuationKey));
            _continuationKey = continuationKey.ToArray();
        }
    }

    private async Task<OperationAuthorization?> AuthorizeOperationAsync(ListActor actor, Guid workspaceId,
        IReadOnlyList<(Guid? ListId, ListAuthorizationAction Action)> requirements, CancellationToken ct)
    {
        if (_authorizationProvider is null) return null;
        var anchor = await ReadAnchorAsync(workspaceId, ct);
        if (anchor is null || anchor.Mode != "host" ||
            !StringComparer.Ordinal.Equals(anchor.Domain, _authorizationDomain) ||
            !StringComparer.Ordinal.Equals(anchor.Scope, actor.TenantId))
            throw NotFound();

        var requests = new List<ListAuthorizationRequest>(requirements.Count + 1)
        {
            new(anchor.Domain!, actor.Subject, anchor.Scope, workspaceId, null, ListAuthorizationAction.UseLists)
        };
        requests.AddRange(requirements.Select(requirement => new ListAuthorizationRequest(anchor.Domain!,
            actor.Subject, anchor.Scope, workspaceId, requirement.ListId, requirement.Action)));

        var decisions = await EvaluateAsync(requests, ct);
        return new OperationAuthorization(anchor, requests.Zip(decisions).ToArray());
    }

    private async Task<OperationAuthorization> AuthorizeAdditionalAsync(ListActor actor,
        OperationAuthorization authorization,
        IReadOnlyList<(Guid? ListId, ListAuthorizationAction Action)> requirements, CancellationToken ct)
    {
        var requests = requirements
            .Distinct()
            .Where(requirement => !authorization.Allows(requirement.ListId, requirement.Action))
            .Select(requirement => new ListAuthorizationRequest(authorization.Anchor.Domain!, actor.Subject,
                authorization.Anchor.Scope, authorization.Anchor.WorkspaceId, requirement.ListId, requirement.Action))
            .ToArray();
        if (requests.Length == 0) return authorization;
        var decisions = await EvaluateAsync(requests, ct);
        return new OperationAuthorization(authorization.Anchor,
            authorization.Decisions.Concat(requests.Zip(decisions)).ToArray());
    }

    private async Task<IReadOnlyList<ListAuthorizationDecision>> EvaluateAsync(
        IReadOnlyList<ListAuthorizationRequest> requests, CancellationToken ct)
    {
        var callStartedUtc = DateTimeOffset.UtcNow;
        var provider = _authorizationProvider ?? throw AuthorizationUnavailable();
        IReadOnlyList<ListAuthorizationDecision> decisions;
        try
        {
            decisions = provider is IListBatchAuthorizationProvider batch
                ? await batch.AuthorizeBatchAsync(requests, ct)
                : await AuthorizeSeparatelyAsync(provider, requests, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ListException) { throw; }
        catch { throw AuthorizationUnavailable(); }
        if (decisions.Count != requests.Count) throw AuthorizationUnavailable();

        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < decisions.Count; index++)
        {
            ListAuthorizationValidation.ValidateDecision(decisions[index], callStartedUtc, now);
            if (!decisions[index].Allowed)
                throw requests[index].ListId is null ? Forbidden() : NotFound();
        }
        return decisions;
    }

    private static async Task<IReadOnlyList<ListAuthorizationDecision>> AuthorizeSeparatelyAsync(
        IListAuthorizationProvider provider, IReadOnlyList<ListAuthorizationRequest> requests, CancellationToken ct)
    {
        var decisions = new List<ListAuthorizationDecision>(requests.Count);
        foreach (var request in requests) decisions.Add(await provider.AuthorizeAsync(request, ct));
        return decisions;
    }

    private async Task<WorkspaceAnchor?> ReadAnchorAsync(Guid workspaceId, CancellationToken ct)
    {
        await EnsureHealthyAsync(ct);
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var batch = new NpgsqlBatch(connection, transaction);
        var configure = new NpgsqlBatchCommand("""
            SELECT set_config('cellbridge.workspace_id',@workspace::text,true),
              set_config('statement_timeout',@timeout::text,true),
              set_config('lock_timeout',@timeout::text,true)
            """);
        configure.Parameters.AddWithValue("workspace", workspaceId);
        configure.Parameters.AddWithValue("timeout",
            Math.Clamp((long)_limits.StatementTimeout.TotalMilliseconds, 1, int.MaxValue));
        batch.BatchCommands.Add(configure);
        var read = new NpgsqlBatchCommand("""
            SELECT workspace_id,authorization_mode,authorization_domain,authorization_scope
            FROM cellbridge_lists.workspaces WHERE workspace_id=@workspace
            """);
        read.Parameters.AddWithValue("workspace", workspaceId);
        batch.BatchCommands.Add(read);
        await using var reader = await batch.ExecuteReaderAsync(ct);
        await reader.NextResultAsync(ct);
        WorkspaceAnchor? result = await reader.ReadAsync(ct)
            ? new WorkspaceAnchor(reader.GetGuid(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct);
        return result;
    }

    private static async Task RequireAnchorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, OperationAuthorization? authorization, bool hostExpected, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT authorization_mode,authorization_domain,authorization_scope
            FROM cellbridge_lists.workspaces WHERE workspace_id=$1
            """, connection, transaction);
        command.Parameters.AddWithValue(workspaceId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw NotFound();
        var current = new WorkspaceAnchor(workspaceId, reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
        if (hostExpected)
        {
            if (authorization is null || current != authorization.Anchor) throw NotFound();
        }
        else if (current.Mode != "builtin") throw NotFound();
    }

    private string ProtectContinuation(ListActor actor, Guid workspaceId, Guid afterCandidateId)
    {
        var payload = new ContinuationPayload(workspaceId, ContinuationContextFingerprint(actor),
            afterCandidateId, DateTimeOffset.UtcNow.AddHours(1));
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_continuationKey, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(ContinuationPurpose));
        var token = Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
        if (token.Length > MaximumContinuationLength)
            throw new InvalidOperationException("The generated list continuation exceeds its encoding bound.");
        return token;
    }

    private ContinuationPayload UnprotectContinuation(string value, ListActor actor, Guid workspaceId)
    {
        try
        {
            if (value.Length > MaximumContinuationLength) throw Validation("The list continuation is invalid.");
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length < 29) throw new CryptographicException();
            var plaintext = new byte[bytes.Length - 28];
            using var aes = new AesGcm(_continuationKey, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext,
                Encoding.UTF8.GetBytes(ContinuationPurpose));
            var payload = JsonSerializer.Deserialize<ContinuationPayload>(plaintext) ?? throw new CryptographicException();
            if (payload.WorkspaceId != workspaceId || payload.AfterCandidateId == Guid.Empty ||
                payload.ContextFingerprint is not { Length: 32 } ||
                !CryptographicOperations.FixedTimeEquals(payload.ContextFingerprint, ContinuationContextFingerprint(actor)) ||
                payload.ExpiresUtc <= DateTimeOffset.UtcNow)
                throw new CryptographicException();
            return payload;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or JsonException)
        { throw Validation("The list continuation is invalid."); }
    }

    private byte[] ContinuationContextFingerprint(ListActor actor) =>
        // A JSON array preserves field boundaries, nulls and exact ordinal identity text.
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new string?[]
        {
            actor.Subject, actor.TenantId, _authorizationProvider is null ? "builtin" : "host", _authorizationDomain
        }));

    private static string ValidateDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 200 ||
            !ListValidation.IsWellFormedText(domain) || domain.Any(char.IsControl))
            throw new ArgumentException("A valid policy domain is required.", nameof(domain));
        return domain;
    }

    private static ListException AuthorizationUnavailable() =>
        new(ListFailure.Unavailable, "List authorization is unavailable.");

    private sealed record WorkspaceAnchor(Guid WorkspaceId, string Mode, string? Domain, string? Scope);
    private sealed record OperationAuthorization(WorkspaceAnchor Anchor,
        IReadOnlyList<(ListAuthorizationRequest First, ListAuthorizationDecision Second)> Decisions)
    {
        internal bool Allows(Guid? listId, ListAuthorizationAction action) =>
            Decisions.Any(pair => pair.Second.Allowed && pair.First.ListId == listId && pair.First.Action == action);

        internal bool Allows(Guid listId, ListAccess access) =>
            (!access.HasFlag(ListAccess.Read) || Allows(listId, ListAuthorizationAction.Read)) &&
            (!access.HasFlag(ListAccess.Write) || Allows(listId, ListAuthorizationAction.Write)) &&
            (!access.HasFlag(ListAccess.Design) || Allows(listId, ListAuthorizationAction.Design));
    }
    private sealed record ContinuationPayload(Guid WorkspaceId, byte[] ContextFingerprint,
        Guid AfterCandidateId, DateTimeOffset ExpiresUtc);
}
