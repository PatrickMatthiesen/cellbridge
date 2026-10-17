using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;
using Xunit.Abstractions;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class ListContinuationBoundaryTests(ListDatabaseFixture database, ITestOutputHelper output)
{
    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maximum_escaped_identities_have_bounded_usable_context_bound_continuations(bool host)
    {
        // This subject is 1024 UTF-16 characters and 1280 UTF-8 bytes, within the native index budget.
        var subject = string.Concat(Enumerable.Repeat("é\"\\/", 256));
        var tenant = string.Concat(Enumerable.Repeat("界\"\\/", 256));
        var domain = string.Concat(Enumerable.Repeat("策\"\\/", 50));
        await using var fixture = await Fixture.CreateAsync(database, host, new(subject, tenant), domain);
        var first = await fixture.Engine.GetListsPageAsync(fixture.Actor, fixture.Workspace, candidateLimit: 1);
        var token = Assert.IsType<string>(first.Continuation);
        output.WriteLine($"host={host}, subjectChars={subject.Length}, tenantChars={tenant.Length}, tokenChars={token.Length}");
        Assert.InRange(token.Length, 1, 512);
        var next = await fixture.Engine.GetListsPageAsync(fixture.Actor, fixture.Workspace, token, 1);
        Assert.Single(first.Lists);
        Assert.Single(next.Lists);
        Assert.NotEqual(first.Lists[0].Id, next.Lists[0].Id);
        Assert.Null(next.Continuation);

        await Invalid(() => fixture.Engine.GetListsPageAsync(fixture.Actor with { Subject = subject[..^1] + "x" }, fixture.Workspace, token, 1));
        await Invalid(() => fixture.Engine.GetListsPageAsync(fixture.Actor with { TenantId = tenant[..^1] + "x" }, fixture.Workspace, token, 1));
        await Invalid(() => fixture.Engine.GetListsPageAsync(fixture.Actor, Guid.NewGuid(), token, 1));
        var differentDomain = new PostgreSqlListEngine(database.RuntimeSource!, null, new AllowAuthority("different-domain"), fixture.Key);
        await Invalid(() => differentDomain.GetListsPageAsync(fixture.Actor, fixture.Workspace, token, 1));
        var differentMode = host ? new PostgreSqlListEngine(database.RuntimeSource!)
            : new PostgreSqlListEngine(database.RuntimeSource!, null, new AllowAuthority(domain), fixture.Key);
        CopyKey(differentMode, fixture.Key);
        await Invalid(() => differentMode.GetListsPageAsync(fixture.Actor, fixture.Workspace, token, 1));
        // Default keys are ephemeral per engine; the private encoding is not a release compatibility contract.
        var recreated = host ? new PostgreSqlListEngine(database.RuntimeSource!, null, new AllowAuthority(domain))
            : new PostgreSqlListEngine(database.RuntimeSource!);
        await Invalid(() => recreated.GetListsPageAsync(fixture.Actor, fixture.Workspace, token, 1));
    }

    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_oversized_tampered_and_expired_tokens_fail_validation(bool host)
    {
        await using var fixture = await Fixture.CreateAsync(database, host, new("subject", "tenant"), "domain");
        var first = await fixture.Engine.GetListsPageAsync(fixture.Actor, fixture.Workspace, candidateLimit: 1);
        var token = Assert.IsType<string>(first.Continuation);
        var tampered = Convert.FromBase64String(token);
        tampered[^1] ^= 1;
        foreach (var invalid in new[] { "", "!", "AA==", new string('A', 4097), token[..^4], Convert.ToBase64String(tampered) })
            await Invalid(() => fixture.Engine.GetListsPageAsync(fixture.Actor, fixture.Workspace, invalid, 1));
        var context = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new string?[]
            { fixture.Actor.Subject, fixture.Actor.TenantId, host ? "host" : "builtin", host ? "domain" : null }));
        foreach (var plaintext in new[] { "null", "{}", "[]", JsonSerializer.Serialize(new {
            WorkspaceId = fixture.Workspace, ContextFingerprint = (byte[]?)null,
            AfterCandidateId = first.Lists[0].Id, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
        }), JsonSerializer.Serialize(new {
            WorkspaceId = fixture.Workspace, ContextFingerprint = context,
            AfterCandidateId = first.Lists[0].Id, ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
        }) })
        {
            var malformed = Encrypt(plaintext, fixture.Key, "CellBridge.Lists.Page.v3");
            await Invalid(() => fixture.Engine.GetListsPageAsync(fixture.Actor, fixture.Workspace, malformed, 1));
        }
        var oldEncoding = Encrypt("null", fixture.Key, "CellBridge.Lists.Page.v2");
        await Invalid(() => fixture.Engine.GetListsPageAsync(fixture.Actor, fixture.Workspace, oldEncoding, 1));
    }

    [PostgreSqlFact]
    public async Task Context_binding_distinguishes_identity_field_boundaries_null_and_ordinal_text()
    {
        await using var fixture = await Fixture.CreateAsync(database, false, new("ab", "c"), "unused");
        var token = (await fixture.Engine.GetListsPageAsync(fixture.Actor, fixture.Workspace, candidateLimit: 1)).Continuation!;
        await Invalid(() => fixture.Engine.GetListsPageAsync(new("a", "bc"), fixture.Workspace, token, 1));
        var actor = fixture.Actor with { TenantId = null };
        token = (await fixture.Engine.GetListsPageAsync(actor, fixture.Workspace, candidateLimit: 1)).Continuation!;
        await Invalid(() => fixture.Engine.GetListsPageAsync(actor with { TenantId = "null" }, fixture.Workspace, token, 1));
        actor = fixture.Actor with { TenantId = "é" };
        token = (await fixture.Engine.GetListsPageAsync(actor, fixture.Workspace, candidateLimit: 1)).Continuation!;
        await Invalid(() => fixture.Engine.GetListsPageAsync(actor with { TenantId = "e\u0301" }, fixture.Workspace, token, 1));
    }

    private static async Task Invalid(Func<Task> attempt) =>
        Assert.Equal(ListFailure.Validation, (await Assert.ThrowsAsync<ListException>(attempt)).Failure);

    private static string Encrypt(string plaintext, byte[] key, string aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[bytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, bytes, ciphertext, tag, Encoding.UTF8.GetBytes(aad));
        return Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
    }

    // Seed native engines with the same key to exercise context checks independently of key mismatch.
    private static void CopyKey(PostgreSqlListEngine engine, byte[] key) => key.CopyTo(
        (byte[])typeof(PostgreSqlListEngine).GetField("_continuationKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!, 0);

    private sealed class Fixture(ListDatabaseFixture database, ListActor actor) : IAsyncDisposable
    {
        public Guid Workspace { get; } = Guid.NewGuid();
        public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
        public ListActor Actor => actor;
        public PostgreSqlListEngine Engine { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(ListDatabaseFixture database, bool host, ListActor actor, string domain)
        {
            var fixture = new Fixture(database, actor);
            try
            {
                if (host)
                    await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, fixture.Workspace, "Token context", domain, actor.TenantId!);
                else
                {
                    await PostgreSqlListSetup.RegisterWorkspaceAsync(database.AdminSource!, fixture.Workspace, "Token context", database.AdminActor.Subject);
                    await database.Engine.SetMembershipAsync(database.AdminActor, fixture.Workspace, actor.Subject, true, false, false);
                }
                fixture.Engine = host ? new(database.RuntimeSource!, null, new AllowAuthority(domain), fixture.Key)
                    : new(database.RuntimeSource!);
                CopyKey(fixture.Engine, fixture.Key);
                for (var index = 0; index < 2; index++)
                {
                    var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
                    var list = await (host ? fixture.Engine : database.Engine).CreateListAsync(host ? actor : database.AdminActor,
                        fixture.Workspace, new(Guid.NewGuid(), "List " + index, [field]));
                    if (!host) await database.Engine.SetListGrantAsync(database.AdminActor, fixture.Workspace, list.Id, actor.Subject, ListAccess.Read);
                }
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await using var connection = await database.AdminSource!.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT set_config('cellbridge.workspace_id',@workspace::text,true);
                DELETE FROM cellbridge_lists.workspaces WHERE workspace_id=@workspace;
                """, connection, transaction);
            command.Parameters.AddWithValue("workspace", Workspace);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
    }
    private sealed class AllowAuthority(string domain) : IListAuthorizationProvider
    {
        public string PolicyDomain => domain;
        public ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(new ListAuthorizationDecision(true));
    }
}
