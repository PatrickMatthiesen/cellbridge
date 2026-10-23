using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

public sealed partial class SqlServerListEngine
{
    private readonly byte[] continuationKey = RandomNumberGenerator.GetBytes(32);
    private const string ContinuationPurpose = "CellBridge.Lists.SqlServer.Page.v1";
    private const int MaximumContinuationLength = 512;

    public async Task<IReadOnlyList<ListDefinition>> GetListsAsync(ListActor actor, Guid workspaceId, CancellationToken ct = default)
    {
        var results = new List<ListDefinition>();
        string? continuation = null;
        do
        {
            var page = await GetListsPageAsync(actor, workspaceId, continuation, Math.Min(256, limits.MaxLists), ct);
            results.AddRange(page.Lists);
            if (results.Count > limits.MaxLists) throw Limit("The workspace list limit has been exceeded.");
            continuation = page.Continuation;
        } while (continuation is not null);
        return results.OrderBy(list => list.Name, StringComparer.Ordinal).ThenBy(list => list.Id).ToArray();
    }

    public async Task<ListDefinitionPage> GetListsPageAsync(ListActor actor, Guid workspaceId, string? continuation = null,
        int candidateLimit = 256, CancellationToken ct = default)
    {
        ListAuthorizationValidation.ValidateActor(actor);
        if (workspaceId == Guid.Empty) throw Validation("A workspace ID is required.");
        if (candidateLimit is < 1 or > 256) throw Validation("The candidate page size must be between 1 and 256.");
        var after = continuation is null ? null : (Guid?)ReadContinuation(continuation, actor, workspaceId);
        var afterKey = after?.ToString("D");
        if (authorizationProvider is null)
            return await OperateAsync(actor, workspaceId, false, async (db, workspace, member, authorization) =>
            {
                var subjectKey = SqlServerListSetup.SubjectKey(actor.Subject);
                var candidates = await (from list in db.Lists.AsNoTracking()
                    join grant in db.ListGrants.AsNoTracking() on new { list.WorkspaceId, list.ListId } equals new { grant.WorkspaceId, grant.ListId }
                    where list.WorkspaceId == workspaceId && EF.Property<byte[]>(grant, "SubjectKey") == subjectKey && grant.Access != 0 &&
                        (afterKey == null || SqlServerListDbContext.KeyCompare(SqlServerListDbContext.UuidKey(list.ListId), afterKey) > 0)
                    orderby EF.Functions.Collate(SqlServerListDbContext.UuidKey(list.ListId), OrdinalCollation)
                    select new { list.ListId, grant.Subject }).Take(candidateLimit + 1).ToListAsync(ct);
                var scanned = candidates.Take(candidateLimit).ToArray();
                var allowed = scanned.Where(row => StringComparer.Ordinal.Equals(row.Subject, actor.Subject)).Select(row => row.ListId).ToArray();
                var definitions = await ReadDefinitionsAsync(db, workspaceId, allowed, ct);
                return new ListDefinitionPage(definitions, candidates.Count > candidateLimit
                    ? WriteContinuation(actor, workspaceId, scanned[^1].ListId) : null);
            }, ct);

        return await SqlServerListDatabase.MapFailuresAsync(async () =>
        {
            var authorization = (await AuthorizeOperationAsync(actor, workspaceId, [], null, ct))!;
            var request = new ListAuthorizationRequest(authorization.Anchor.Domain!, actor.Subject, authorization.Anchor.Scope,
                workspaceId, null, ListAuthorizationAction.Read);
            var started = DateTimeOffset.UtcNow;
            var scope = ListQueryAuthorizationScope.CheckCandidates;
            if (authorizationProvider is IListQueryAuthorizationProvider queryProvider)
            {
                ListQueryAuthorizationDecision decision;
                try { decision = await queryProvider.AuthorizeQueryAsync(request, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { throw ListAuthorizationValidation.Unavailable(); }
                ListAuthorizationValidation.ValidateQueryDecision(decision, request, started, DateTimeOffset.UtcNow);
                scope = decision.Scope;
                authorization.Decisions.Add((request, new ListAuthorizationDecision(true, decision.EvaluatedAtUtc, decision.FreshUntilUtc), started));
            }
            if (scope == ListQueryAuthorizationScope.None)
                return await OperateAsync(actor, workspaceId, false, (db, workspace, member, auth) =>
                    Task.FromResult(new ListDefinitionPage([], null)), ct, admittedAuthorization: authorization);

            // Only bounded candidate IDs leave the first transaction. No list
            // definitions are read until the host has allowed their IDs.
            var candidates = await OperateAsync(actor, workspaceId, false, async (db, workspace, member, auth) =>
                await db.Lists.AsNoTracking().Where(row => row.WorkspaceId == workspaceId &&
                    (afterKey == null || SqlServerListDbContext.KeyCompare(SqlServerListDbContext.UuidKey(row.ListId), afterKey) > 0))
                    .OrderBy(row => EF.Functions.Collate(SqlServerListDbContext.UuidKey(row.ListId), OrdinalCollation))
                    .Select(row => row.ListId).Take(candidateLimit + 1).ToListAsync(ct), ct, admittedAuthorization: authorization);
            var scanned = candidates.Take(candidateLimit).ToArray();
            var allowed = scope == ListQueryAuthorizationScope.All
                ? scanned
                : await AuthorizeCandidatesAsync(authorization, actor, scanned, ct);
            var definitions = await OperateAsync(actor, workspaceId, false, (db, workspace, member, auth) =>
                ReadDefinitionsAsync(db, workspaceId, allowed, ct), ct, admittedAuthorization: authorization);
            return new ListDefinitionPage(definitions, candidates.Count > candidateLimit
                ? WriteContinuation(actor, workspaceId, scanned[^1]) : null);
        }, ct);
    }

    private async Task<Guid[]> AuthorizeCandidatesAsync(OperationAuthorization authorization, ListActor actor, Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var requests = ids.Select(id => new ListAuthorizationRequest(authorization.Anchor.Domain!, actor.Subject, authorization.Anchor.Scope,
            authorization.Anchor.WorkspaceId, id, ListAuthorizationAction.Read)).ToArray();
        var started = DateTimeOffset.UtcNow;
        IReadOnlyList<ListAuthorizationDecision> decisions;
        try
        {
            if (authorizationProvider is IListBatchAuthorizationProvider batch) decisions = await batch.AuthorizeBatchAsync(requests, ct);
            else
            {
                var separate = new List<ListAuthorizationDecision>();
                foreach (var request in requests) separate.Add(await authorizationProvider!.AuthorizeAsync(request, ct));
                decisions = separate;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw ListAuthorizationValidation.Unavailable(); }
        if (decisions is null || decisions.Count != requests.Length) throw ListAuthorizationValidation.Unavailable();
        for (var index = 0; index < decisions.Count; index++)
        {
            if (decisions[index] is null) throw ListAuthorizationValidation.Unavailable();
            ListAuthorizationValidation.ValidateDecision(decisions[index], started, DateTimeOffset.UtcNow);
            authorization.Decisions.Add((requests[index], decisions[index], started));
        }
        return ids.Where((_, index) => decisions[index].Allowed).ToArray();
    }

    private static async Task<IReadOnlyList<ListDefinition>> ReadDefinitionsAsync(SqlServerListDbContext db, Guid workspaceId,
        Guid[] allowed, CancellationToken ct)
    {
        if (allowed.Length == 0) return [];
        var rows = await (from list in db.Lists.AsNoTracking()
            join schema in db.ListSchemas.AsNoTracking() on new { list.WorkspaceId, list.ListId, list.SchemaVersion }
                equals new { schema.WorkspaceId, schema.ListId, schema.SchemaVersion }
            where list.WorkspaceId == workspaceId && allowed.Contains(list.ListId)
            orderby EF.Functions.Collate(SqlServerListDbContext.UuidKey(list.ListId), OrdinalCollation)
            select new { list.ListId, schema.Definition }).ToListAsync(ct);
        return rows.Select(row =>
        {
            var definition = ReadDefinition(row.Definition);
            if (definition.Id != row.ListId || definition.WorkspaceId != workspaceId) throw InvalidStored();
            return definition;
        }).ToArray();
    }

    private byte[] ContinuationFingerprint(ListActor actor) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new string?[]
        { actor.Subject, actor.TenantId, authorizationProvider is null ? "builtin" : "host", authorizationDomain }));

    private string WriteContinuation(ListActor actor, Guid workspaceId, Guid after)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new ContinuationPayload(workspaceId, ContinuationFingerprint(actor), after, DateTimeOffset.UtcNow.AddHours(1)), JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(continuationKey, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(ContinuationPurpose));
        var token = Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
        if (token.Length > MaximumContinuationLength) throw new InvalidOperationException("The list continuation exceeds its encoding bound.");
        return token;
    }

    private Guid ReadContinuation(string token, ListActor actor, Guid workspaceId)
    {
        try
        {
            if (token.Length > MaximumContinuationLength) throw Validation("The list continuation is invalid.");
            var bytes = Convert.FromBase64String(token);
            if (bytes.Length < 29) throw new CryptographicException();
            var plaintext = new byte[bytes.Length - 28];
            using var aes = new AesGcm(continuationKey, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext, Encoding.UTF8.GetBytes(ContinuationPurpose));
            var payload = JsonSerializer.Deserialize<ContinuationPayload>(plaintext, JsonOptions) ?? throw new CryptographicException();
            if (payload.WorkspaceId != workspaceId || payload.After == Guid.Empty || payload.Fingerprint is not { Length: 32 } ||
                !CryptographicOperations.FixedTimeEquals(payload.Fingerprint, ContinuationFingerprint(actor)) || payload.Expires <= DateTimeOffset.UtcNow)
                throw new CryptographicException();
            return payload.After;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        { throw Validation("The list continuation is invalid."); }
    }

    private sealed record ContinuationPayload(Guid WorkspaceId, byte[] Fingerprint, Guid After, DateTimeOffset Expires);
}
