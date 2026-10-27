using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CellBridge.Lists;
using CellBridge.Lists.AspNetCore;
using CellBridge.Lists.PostgreSql;
using CellBridge.Lists.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.CSharp;
using Npgsql;

internal sealed class ListsLoadScenario(bool http, LoadResources resources) : IAsyncDisposable
{
    internal const string RevisionConflict = "revision_conflict";
    private static readonly ListActor Actor = new("local:integration-writer");
    private static readonly Guid Title = Guid.Parse("ba000000-0000-0000-0000-000000000001");
    private static readonly Guid Counter = Guid.Parse("ba000000-0000-0000-0000-000000000002");
    private static readonly Guid Payload = Guid.Parse("ba000000-0000-0000-0000-000000000003");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
    private readonly ConcurrentDictionary<Guid, SavedRecord> records = [];
    private IListEngine engine = null!;
    private NpgsqlDataSource? runtime;
    private HttpClient? client;
    private ListDefinition[] lists = [];
    private Target[] targets = [];
    private ListsLoadSettings settings = new();
    private string payload = "";
    private bool initialized;
    private long counter;

    public ScenarioProps Create() => CellBridgeLoadTests.Defaults(Scenario.Create(http ? "lists-http" : "lists-provider", async context =>
    {
        resources.Observe(context);
        try
        {
            if (!context.ScenarioInstanceData.TryGetValue("lists-random", out var randomState))
            {
                randomState = new Random(unchecked(settings.Seed + context.ScenarioInfo.InstanceNumber * 397));
                context.ScenarioInstanceData["lists-random"] = randomState;
            }
            var random = (Random)randomState;
            var preferred = context.ScenarioInfo.InstanceNumber % settings.Clients;
            if (settings.Workload == "smoke")
            {
                var list = lists[preferred % lists.Length];
                var created = await CreateAsync(list, context);
                await PatchAsync(new(list, created.Id), context);
                await QueryAsync(list, context, 2);
            }
            else
            {
                if (settings.Workload != "contention" && context.ScenarioInfo.InstanceNumber >= settings.Clients)
                    throw new InvalidDataException("Lists Clients must cover the scenario's instance numbers.");
                var target = targets[settings.Workload == "contention" ? 0 : preferred];
                var choice = random.Next(settings.GetWeight + settings.QueryWeight + settings.PatchWeight + settings.CreateWeight);
                if (choice < settings.GetWeight) await GetAsync(target, context);
                else if ((choice -= settings.GetWeight) < settings.QueryWeight) await QueryAsync(target.List, context, settings.QueryPages);
                else if ((choice -= settings.QueryWeight) < settings.PatchWeight) await PatchAsync(target, context);
                else await CreateAsync(target.List, context);
            }
            return Response.Ok();
        }
        catch (ExpectedConflictException)
        {
            // NBomber reports these separately from successful PATCH requests. The next
            // iteration starts a new operation; it does not retry the rejected write.
            return Response.Fail(statusCode: RevisionConflict);
        }
        catch (InvalidDataException)
        {
            resources.Stop("Lists correctness check failed");
            return Response.Fail(statusCode: nameof(InvalidDataException));
        }
        catch (OperationCanceledException) when (resources.CancellationToken.IsCancellationRequested)
        { return Response.Fail(statusCode: "cancelled"); }
        catch (RequestFailureException error) { return Response.Fail(statusCode: error.StatusCode); }
        catch (Exception error) { return Response.Fail(statusCode: error.GetType().Name); }
    })).WithMaxFailCount(int.MaxValue).WithThresholds().WithInit(context => resources.InitializeAsync(async () =>
    {
        settings = context.CustomSettings.Get<ListsLoadSettings>() ?? new();
        settings.Validate();
        payload = new string('x', settings.PayloadBytes);
        var environment = await resources.EnvironmentAsync();
        var sqlServer = environment.Backend == "sqlserver";
        if (sqlServer)
        {
            var options = new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(environment.ListsRuntimeConnectionString).Options;
            var sqlEngine = new SqlServerListEngine(options);
            await sqlEngine.CheckHealthAsync();
            engine = sqlEngine;
        }
        else
        {
            runtime = NpgsqlDataSource.Create(environment.ListsRuntimeConnectionString);
            var postgresEngine = new PostgreSqlListEngine(runtime);
            await postgresEngine.CheckHealthAsync();
            engine = postgresEngine;
        }
        if (http) client = await environment.CreateClientAsync(resources.CancellationToken);
        lists = new ListDefinition[settings.Workload is "same-workspace" or "contention" ? 1 : settings.Workspaces];
        await using var admin = sqlServer ? null : NpgsqlDataSource.Create(environment.ListsConnectionString);
        var sqlAdmin = sqlServer
            ? new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(environment.ListsConnectionString).Options
            : null;
        for (var workspace = 0; workspace < lists.Length; workspace++)
        {
            var workspaceId = Guid.NewGuid();
            if (sqlAdmin is not null)
                await SqlServerListSetup.RegisterWorkspaceAsync(sqlAdmin, workspaceId, "Load workspace", Actor.Subject, resources.CancellationToken);
            else
                await PostgreSqlListSetup.RegisterWorkspaceAsync(admin!, workspaceId, "Load workspace", Actor.Subject, resources.CancellationToken);
            var list = await engine.CreateListAsync(Actor, workspaceId, new(Guid.NewGuid(),
                http ? "HTTP load" : "Provider load", [new(Title, "Title", ListFieldType.Text),
                    new(Counter, "Counter", ListFieldType.Integer), new(Payload, "Payload", ListFieldType.Text, MaxLength: settings.PayloadBytes)]), resources.CancellationToken);
            lists[workspace] = list;
            // Keep paging fixtures immutable. Client records use negative counters.
            for (var i = 0; i < settings.Records; i++)
                await engine.CreateRecordAsync(Actor, list.WorkspaceId, list.Id,
                    new(Guid.NewGuid(), list.SchemaVersion, Values("Query fixture", i)), resources.CancellationToken);
        }
        targets = new Target[settings.Workload == "contention" ? 1 : settings.Clients];
        for (var i = 0; i < targets.Length; i++)
        {
            var list = lists[i % lists.Length];
            var created = await engine.CreateRecordAsync(Actor, list.WorkspaceId, list.Id,
                new(Guid.NewGuid(), list.SchemaVersion, Values("Seeded client record", -1)), resources.CancellationToken);
            targets[i] = new(list, created.Id);
            records[created.Id] = new(list, -1, 1, "Seeded client record", 0);
        }
        initialized = true;
    }));

    private async Task<CreateRecordResult> CreateAsync(ListDefinition list, IScenarioContext context)
    {
        var value = -Interlocked.Increment(ref counter);
        var request = new CreateRecordRequest(Guid.NewGuid(), list.SchemaVersion, Values("Created by load test", value));
        var created = await RequestAsync("create", context, async () =>
        {
            var result = http
                ? await SendAsync<CreateRecordResult>(Path(list), HttpMethod.Post, request)
                : await engine.CreateRecordAsync(Actor, list.WorkspaceId, list.Id, request, resources.CancellationToken);
            if (result.Id != request.Id || result.Replayed || result.Revision != 1)
                throw new InvalidDataException("Create did not create a fresh record.");
            return result;
        });
        records[created.Id] = new(list, value, 1, "Created by load test", 0);
        return created;
    }

    private Task<ListRecord> GetAsync(Target target, IScenarioContext context) => RequestAsync("get", context, async () =>
    {
        var record = http
            ? await SendAsync<ListRecord>(Path(target.List) + "/" + target.Id, HttpMethod.Get)
            : await engine.GetRecordAsync(Actor, target.List.WorkspaceId, target.List.Id, target.Id, resources.CancellationToken);
        if (record.Id != target.Id || record.ListId != target.List.Id || record.WorkspaceId != target.List.WorkspaceId ||
            record.SchemaVersion != target.List.SchemaVersion || record.Revision < 1 ||
            record.Values[Payload.ToString("D")]!.GetValue<string>() != payload)
            throw new InvalidDataException("Get returned an unexpected record.");
        return record;
    });

    private async Task PatchAsync(Target target, IScenarioContext context)
    {
        var current = await GetAsync(target, context);
        var title = "Committed write " + Interlocked.Increment(ref counter);
        var patch = new UpdateRecordRequest(current.Revision, target.List.SchemaVersion, [new(Title, JsonValue.Create(title))]);
        var updated = await RequestAsync("patch", context, async () =>
        {
            var record = http
                ? await SendAsync<ListRecord>(Path(target.List) + "/" + target.Id, HttpMethod.Patch, patch)
                : await engine.UpdateRecordAsync(Actor, target.List.WorkspaceId, target.List.Id, target.Id, patch, resources.CancellationToken);
            if (record.Id != target.Id || record.Revision != current.Revision + 1 ||
                record.Values[Title.ToString("D")]!.GetValue<string>() != title)
                throw new InvalidDataException("Patch did not save the expected revision and value.");
            return record;
        }, allowConflict: settings.Workload == "contention");
        records.AddOrUpdate(target.Id,
            _ => throw new InvalidDataException("An unseeded record was patched."),
            (_, previous) => previous with
            {
                Commits = previous.Commits + 1,
                Revision = Math.Max(previous.Revision, updated.Revision),
                Title = updated.Revision > previous.Revision ? title : previous.Title,
            });
    }

    private async Task QueryAsync(ListDefinition list, IScenarioContext context, int pages)
    {
        var query = new ListQuery([new(Counter, FilterOperator.GreaterOrEqual, JsonValue.Create(settings.Records / 2))], Counter, Limit: settings.PageSize);
        Page? previous = null;
        for (var i = 0; i < pages; i++)
        {
            var cursor = previous?.Cursor;
            var request = query with { AfterRecordId = previous?.AfterId };
            var expectedFirst = settings.Records / 2 + i * settings.PageSize;
            var page = await RequestAsync("query", context, async () =>
            {
                Page result;
                if (http)
                {
                    var response = await SendAsync<ListQueryResponse>(Path(list) + "/query", HttpMethod.Post, new ListQueryRequest(request, cursor));
                    if (response.SchemaVersion != list.SchemaVersion) throw new InvalidDataException("Query used the wrong schema.");
                    result = new(response.Records, response.NextCursor, null);
                }
                else
                {
                    var response = await engine.QueryAsync(Actor, list.WorkspaceId, list.Id, request, resources.CancellationToken);
                    if (response.SchemaVersion != list.SchemaVersion) throw new InvalidDataException("Query used the wrong schema.");
                    result = new(response.Records, null, response.NextRecordId);
                }
                if (result.Records.Count != settings.PageSize || result.Records.Where((record, n) =>
                    record.WorkspaceId != list.WorkspaceId || record.ListId != list.Id ||
                    record.Values[Counter.ToString("D")]!.GetValue<long>() != expectedFirst + n).Any())
                    throw new InvalidDataException("Query did not return the expected ordered page.");
                if (previous is not null && previous.Records.Select(r => r.Id).Intersect(result.Records.Select(r => r.Id)).Any())
                    throw new InvalidDataException("Paging repeated records.");
                if (i + 1 < pages && result.Cursor is null && result.AfterId is null)
                    throw new InvalidDataException("The fixture has no next page.");
                return result;
            });
            previous = page;
        }
    }

    private async Task<T> RequestAsync<T>(string name, IScenarioContext context, Func<Task<T>> action, bool allowConflict = false)
    {
        var response = await Step.Run(name, context, async () =>
        {
            try { return Response.Ok(await action(), statusCode: name == "create" ? "201" : "200"); }
            catch (HttpRequestException error) when (allowConflict && error.StatusCode == HttpStatusCode.Conflict)
            { return Response.Fail<T>(statusCode: RevisionConflict); }
            catch (ListException error) when (allowConflict && error.Failure == ListFailure.Conflict)
            { return Response.Fail<T>(statusCode: RevisionConflict); }
            catch (HttpRequestException error)
            { return Response.Fail<T>(statusCode: error.StatusCode is { } code ? ((int)code).ToString() : error.GetType().Name); }
            catch (ListException error) { return Response.Fail<T>(statusCode: error.Failure.ToString()); }
            catch (Exception error) { return Response.Fail<T>(statusCode: error.GetType().Name); }
        });
        if (response.IsError)
        {
            resources.CancellationToken.ThrowIfCancellationRequested();
            if (response.StatusCode == RevisionConflict) throw new ExpectedConflictException();
            if (response.StatusCode == nameof(InvalidDataException))
                throw new InvalidDataException($"Lists {name} returned an unexpected result.");
            throw new RequestFailureException(response.StatusCode);
        }
        return response.Payload.Value!;
    }

    private static string Path(ListDefinition list) => $"/api/workspaces/{list.WorkspaceId:D}/lists/{list.Id:D}/records";
    private JsonObject Values(string title, long value) => new()
    {
        [Title.ToString("D")] = title, [Counter.ToString("D")] = value, [Payload.ToString("D")] = payload,
    };

    private async Task<T> SendAsync<T>(string path, HttpMethod method, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await client!.SendAsync(request, resources.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, resources.CancellationToken)
            ?? throw new InvalidDataException("The API returned no JSON result.");
    }

    public async Task VerifyAsync()
    {
        if (!initialized) return;
        foreach (var (id, expected) in records)
        {
            var record = await engine.GetRecordAsync(Actor, expected.List.WorkspaceId, expected.List.Id, id);
            if (record.Revision != expected.Revision || record.Revision != expected.Commits + 1 ||
                record.Values[Title.ToString("D")]!.GetValue<string>() != expected.Title ||
                record.Values[Counter.ToString("D")]!.GetValue<long>() != expected.Value ||
                record.Values[Payload.ToString("D")]!.GetValue<string>() != payload)
                throw new InvalidDataException("Final record differs from successful commits.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (runtime is not null) await runtime.DisposeAsync();
    }

    private sealed record Target(ListDefinition List, Guid Id);
    private sealed record SavedRecord(ListDefinition List, long Value, long Revision, string Title, long Commits);
    private sealed record Page(IReadOnlyList<ListRecord> Records, string? Cursor, Guid? AfterId);
    private sealed class ExpectedConflictException : Exception;
    private sealed class RequestFailureException(string statusCode) : Exception
    {
        public string StatusCode { get; } = statusCode;
    }
}

internal sealed class ListsLoadSettings
{
    public string Workload { get; init; } = "independent";
    public int Clients { get; init; } = 64;
    public int Seed { get; init; } = 1729;
    public int GetWeight { get; init; } = 40;
    public int QueryWeight { get; init; } = 20;
    public int PatchWeight { get; init; } = 35;
    public int CreateWeight { get; init; } = 5;
    public int QueryPages { get; init; } = 1;
    public int Workspaces { get; init; } = 1;
    public int Records { get; init; } = 128;
    public int PageSize { get; init; } = 16;
    public int PayloadBytes { get; init; } = 1024;
    public void Validate()
    {
        if (Workload is not ("independent" or "same-workspace" or "contention" or "smoke") ||
            Clients is < 1 or > 10000 || QueryPages is < 1 or > 2 ||
            GetWeight < 0 || QueryWeight < 0 || PatchWeight < 0 || CreateWeight < 0 ||
            (long)GetWeight + QueryWeight + PatchWeight + CreateWeight is < 1 or > int.MaxValue ||
            Workspaces is < 1 or > 128 || PageSize <= 0 || PageSize > 500 || Records < 4 * PageSize ||
            PayloadBytes <= 0 || PayloadBytes > 32 * 1024)
            throw new ArgumentException("Lists workload, client capacity, operation weights, or fixture bounds are invalid.");
    }
}
