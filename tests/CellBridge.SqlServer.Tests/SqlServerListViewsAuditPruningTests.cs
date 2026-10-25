using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

public sealed partial class SqlServerListEngineTests
{
    [Fact]
    public async Task ViewsEnforceDesignReadFieldAndQueryBoundsAndExecuteSavedFilters()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var request = Request(list);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        await env.Engine.SetMembershipAsync(Admin, env.Workspace, "reader", true, false, false);
        await env.Engine.SetListGrantAsync(Admin, env.Workspace, list.Id, "reader", ListAccess.Read);
        var id = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
        var viewRequest = new SaveViewRequest(id, "View", new([new(list.Fields[0].Id, FilterOperator.Equal, JsonValue.Create("initial"))], list.Fields[0].Id, Limit: 1), [list.Fields[0].Id]);
        await Failure(ListFailure.NotFound, () => env.Engine.SaveViewAsync(new("reader"), env.Workspace, list.Id, viewRequest));
        var view = await env.Engine.SaveViewAsync(Admin, env.Workspace, list.Id, viewRequest);
        Assert.Equal(id, view.Id);
        Assert.Equal(request.Id, Assert.Single((await env.Engine.QueryAsync(new("reader"), env.Workspace, list.Id,
            Assert.Single(await env.Engine.GetViewsAsync(new("reader"), env.Workspace, list.Id)).Query)).Records).Id);
        var limited = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxViewsPerList = 1 });
        await Failure(ListFailure.Limit, () => limited.SaveViewAsync(Admin, env.Workspace, list.Id, viewRequest with { Id = Guid.NewGuid() }));
        await limited.SaveViewAsync(Admin, env.Workspace, list.Id, viewRequest with { Name = "Updated" });
        var second = Guid.Parse("00000001-0000-0000-0000-ffffffffffff");
        await env.Engine.SaveViewAsync(Admin, env.Workspace, list.Id, viewRequest with { Id = second });
        Assert.Equal(new[] { second, id }, (await env.Engine.GetViewsAsync(Admin, env.Workspace, list.Id)).Select(value => value.Id));
        foreach (var invalid in new[]
        {
            viewRequest with { VisibleFields = [Guid.NewGuid()] },
            viewRequest with { VisibleFields = [list.Fields[0].Id, list.Fields[0].Id] },
            viewRequest with { Query = new(SortFieldId: Guid.NewGuid()) },
            viewRequest with { Query = new(Limit: 501) },
            viewRequest with { Query = new([new(list.Fields[0].Id, FilterOperator.Equal, new JsonArray(1))]) }
        })
        {
            var failure = invalid.Query.Limit > 500 ? ListFailure.Limit : ListFailure.Validation;
            await Failure(failure, () => env.Engine.SaveViewAsync(Admin, env.Workspace, list.Id, invalid));
        }
        await env.Engine.SetListGrantAsync(Admin, env.Workspace, list.Id, "reader", ListAccess.None);
        await Failure(ListFailure.NotFound, () => env.Engine.GetViewsAsync(new("reader"), env.Workspace, list.Id));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 9)]
    [InlineData(true, 0)]
    [InlineData(true, 9)]
    public async Task ViewAuditSuppressionAndQuotaFailureRollBackNewAndExistingViews(bool existing, int fakeCount)
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var request = new SaveViewRequest(Guid.NewGuid(), "Original", new(Limit: 1), [list.Fields[0].Id]);
        if (existing) await env.Engine.SaveViewAsync(Admin, env.Workspace, list.Id, request);
        var before = await env.SnapshotAsync(list.Id);
        var suppress = new SqlServerListEngine(env.OptionsWith(new SuppressAuditSave(fakeCount)));
        await Failure(ListFailure.Unavailable, () => suppress.SaveViewAsync(Admin, env.Workspace, list.Id, request with { Name = "Changed" }));
        Assert.Equal(before, await env.SnapshotAsync(list.Id));
        var quota = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxAuditEvents = before.Audits });
        await Failure(ListFailure.Limit, () => quota.SaveViewAsync(Admin, env.Workspace, list.Id, request with { Name = "No quota" }));
        Assert.Equal(before, await env.SnapshotAsync(list.Id));
        var views = await env.Engine.GetViewsAsync(Admin, env.Workspace, list.Id);
        if (existing) Assert.Equal("Original", Assert.Single(views).Name);
        else Assert.Empty(views);
        await env.Engine.SaveViewAsync(Admin, env.Workspace, list.Id, request);
        Assert.Single(await env.Engine.GetViewsAsync(Admin, env.Workspace, list.Id));
    }

    [Fact]
    public async Task PersistedViewInterceptorValuesDetermineResponseAuditAndLogicalCharge()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var before = await env.SnapshotAsync(list.Id);
        var rewrite = new RewriteView();
        var saved = await new SqlServerListEngine(env.OptionsWith(rewrite)).SaveViewAsync(Admin, env.Workspace, list.Id,
            new(Guid.NewGuid(), "Caller", new(Limit: 1), [list.Fields[0].Id]));
        Assert.Equal("é persisted", saved.Name);
        Assert.Empty(saved.VisibleFields);
        var json = JsonSerializer.Serialize(saved, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var after = await env.SnapshotAsync(list.Id);
        var second = await new SqlServerListEngine(env.OptionsWith(new RewriteView("another persisted name"))).SaveViewAsync(
            Admin, env.Workspace, list.Id, new(Guid.NewGuid(), "Caller", new(Limit: 1), [list.Fields[0].Id]));
        var secondJson = JsonSerializer.Serialize(second, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(Encoding.UTF8.GetByteCount(secondJson) - Encoding.UTF8.GetByteCount(json),
            ((await env.SnapshotAsync(list.Id)).Bytes - after.Bytes) - (after.Bytes - before.Bytes));
        var audit = (await env.Engine.GetAuditAsync(Admin, env.Workspace, 1)).Single();
        Assert.Equal("view.save", audit.Action);
        Assert.Empty(audit.FieldIds);
    }

    [Fact]
    public async Task AuditRetrievalRequiresManageAuthorityAndHonorsBoundsAndHostPermission()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        await env.Engine.SetMembershipAsync(Admin, env.Workspace, "creator", true, true, false);
        await env.Engine.SetListGrantAsync(Admin, env.Workspace, list.Id, "creator", ListAccess.Read | ListAccess.Write | ListAccess.Design);
        await Failure(ListFailure.Forbidden, () => env.Engine.GetAuditAsync(new("creator"), env.Workspace));
        await Failure(ListFailure.Forbidden, () => env.Engine.GetAuditAsync(new("missing"), env.Workspace));
        await Failure(ListFailure.Validation, () => env.Engine.GetAuditAsync(Admin, env.Workspace, 0));
        await Failure(ListFailure.Validation, () => env.Engine.GetAuditAsync(Admin, env.Workspace, 1001));
        var request = Request(list);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        var latest = Assert.Single(await env.Engine.GetAuditAsync(Admin, env.Workspace, 1));
        Assert.Equal("record.create", latest.Action);
        Assert.Equal(request.Id, latest.RecordId);
        Assert.Equal(Admin.Subject, latest.Actor);
        Assert.Equal(list.Fields[0].Id, Assert.Single(latest.FieldIds));
        var audit = await env.Engine.GetAuditAsync(Admin, env.Workspace, 2);
        Assert.Equal(2, audit.Count);
        Assert.True(audit[0].Id > audit[1].Id);
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host", "tenant");
        var policy = new HostPolicy("policy");
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", "policy", "tenant");
        var host = new SqlServerListEngine(env.RuntimeOptions, null, policy);
        await host.CreateListAsync(actor, workspace, NewListRequest());
        Assert.Single(await host.GetAuditAsync(actor, workspace));
        policy.Allow = decision => decision.Action != ListAuthorizationAction.ReadAudit;
        await Failure(ListFailure.Forbidden, () => host.GetAuditAsync(actor, workspace));
        await Failure(ListFailure.NotFound, () => host.GetAuditAsync(actor with { TenantId = "different" }, workspace));
    }

    [Fact]
    public async Task ExpirationPrunesOnlyAfterWriteAuthorityReclaimsQuotaOnceAndNeverReleasesIdentity()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var request = Request(list);
        var empty = await env.SnapshotAsync(list.Id);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        var createBytes = (await env.SnapshotAsync(list.Id)).Bytes - empty.Bytes;
        await env.Engine.SetMembershipAsync(Admin, env.Workspace, "reader", true, false, false);
        await env.Engine.SetListGrantAsync(Admin, env.Workspace, list.Id, "reader", ListAccess.Read);
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, env.Workspace))
        {
            (await scope.Context.CreateReceipts.SingleAsync()).ExpiresUtc = await SqlServerListDatabase.ClockAsync(scope.Context, default);
            await scope.Context.SaveChangesAsync();
            await scope.CommitAsync();
        }
        var before = await env.SnapshotAsync(list.Id);
        var fresh = request with { Id = Guid.NewGuid() };
        var max = before.Bytes + createBytes - 1;
        var quota = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxWorkspaceBytes = max });
        await Failure(ListFailure.Limit, () => quota.CreateRecordAsync(Admin, env.Workspace, list.Id, fresh));
        await Failure(ListFailure.NotFound, () => env.Engine.CreateRecordAsync(new("reader"), env.Workspace, list.Id, request));
        Assert.Equal(before, await env.SnapshotAsync(list.Id));
        await Failure(ListFailure.Unavailable, () => new SqlServerListEngine(env.OptionsWith(new SuppressAllSaves()))
            .CreateRecordAsync(Admin, env.Workspace, list.Id, request));
        Assert.Equal(before, await env.SnapshotAsync(list.Id));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqlServerListEngine(env.OptionsWith(new CancelAnySave(cancellation)))
            .CreateRecordAsync(Admin, env.Workspace, list.Id, request, cancellation.Token));
        Assert.Equal(before, await env.SnapshotAsync(list.Id));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Failure(ListFailure.Conflict,
            () => env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request))));
        var pruned = await env.SnapshotAsync(list.Id);
        Assert.True(pruned.Bytes < before.Bytes);
        Assert.Equal(before with { Bytes = pruned.Bytes, Receipts = 0 }, pruned);
        await Failure(ListFailure.Conflict, () => env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request));
        Assert.Equal(pruned, await env.SnapshotAsync(list.Id));
        Assert.Equal(2, (await quota.CreateRecordAsync(Admin, env.Workspace, list.Id, fresh)).Number);
        var after = await env.SnapshotAsync(list.Id);
        Assert.Equal(pruned.Bytes + createBytes, after.Bytes);
        Assert.True(after.Bytes <= max);
        Assert.Equal(2, after.Claims);
        Assert.Equal(2, after.Identities);
        Assert.Equal(2, after.Records);
        Assert.Equal(1, after.Receipts);
    }

    private sealed class RewriteView(string name = "é persisted") : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<SavedViewRow>().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
            {
                var json = JsonNode.Parse(entry.Entity.Definition)!.AsObject();
                json["name"] = name;
                json["visibleFields"] = new JsonArray();
                entry.Entity.Definition = json.ToJsonString();
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CancelAnySave(CancellationTokenSource source) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        { source.Cancel(); return ValueTask.FromResult(result); }
    }
}
