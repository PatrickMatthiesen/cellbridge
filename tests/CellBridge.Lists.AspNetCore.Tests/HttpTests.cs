using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CellBridge.Lists;
using CellBridge.Lists.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace CellBridge.Lists.AspNetCore.Tests;

public sealed partial class HttpTests
{
    [Fact]
    public async Task Concurrent_limit_covers_native_exports_and_odata_and_recovers_after_failure()
    {
        await using var host = await Host.Create(configure: o => o.MaxConcurrentRequests = 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Engine.QueryWait = async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            throw new ListException(ListFailure.Unavailable, "secret backend details");
        };
        var held = host.Post("/records/query", "{\"query\":{\"limit\":1}}");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var calls = host.Engine.Calls;
        try
        {
            foreach (var suffix in new[] { "", "/export.csv", "/export.xlsx", "/odata/$metadata", "/odata/Records" })
            {
                var saturated = await host.Client.GetAsync(host.Path + suffix);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, saturated.StatusCode);
                Assert.Equal("{\"error\":\"Unavailable\"}", await saturated.Content.ReadAsStringAsync());
                Assert.Contains("no-store", saturated.Headers.CacheControl!.ToString());
            }
            Assert.Equal(calls, host.Engine.Calls);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await held).StatusCode);
        host.Engine.QueryWait = null;
        Assert.Equal(HttpStatusCode.OK, (await host.Post("/records/query", "{\"query\":{\"limit\":1}}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post("/records/query", "{\"query\":null}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.Path + "/export.csv")).StatusCode);
        AssertPartitionsEmpty(host);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public void Concurrent_request_limit_requires_bounded_positive_configuration(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCellBridgeLists(o => o.MaxConcurrentRequests = limit));
    }

    [Fact]
    public async Task Cancelled_request_releases_concurrency_slot()
    {
        await using var host = await Host.Create(configure: o => o.MaxConcurrentRequests = 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Engine.QueryWait = async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cancelled.SetResult(); }
        };
        using var requestCancellation = new CancellationTokenSource();
        var held = host.Client.PostAsync(host.Path + "/records/query",
            new StringContent("{\"query\":{\"limit\":1}}", Encoding.UTF8, "application/json"), requestCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requestCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await held);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Engine.QueryWait = null;
        // The client cancellation can complete before the server's response
        // execution finishes. Bound the wait for its finally block to run.
        HttpStatusCode status = HttpStatusCode.ServiceUnavailable;
        for (var attempt = 0; attempt < 20 && status == HttpStatusCode.ServiceUnavailable; attempt++)
        {
            status = (await host.Client.GetAsync(host.Path)).StatusCode;
            if (status == HttpStatusCode.ServiceUnavailable) await Task.Delay(10);
        }
        Assert.Equal(HttpStatusCode.OK, status);
        AssertPartitionsEmpty(host);
    }

    [Fact]
    public async Task Unauthenticated_and_unconfigured_actor_fail_closed()
    {
        await using var host = await Host.Create();
        host.Client.DefaultRequestHeaders.Remove("X-Test-Actor");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(host.Path)).StatusCode);
        await using var unconfigured = await Host.Create(configure: o => o.ActorResolver = null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await unconfigured.Client.GetAsync(unconfigured.Path)).StatusCode);
        Assert.Equal(0, unconfigured.Engine.Calls);
    }

    [Fact]
    public async Task Authorization_error_hides_engine_details_and_workspace_metadata()
    {
        await using var host = await Host.Create();
        var response = await host.Client.GetAsync(host.Path.Replace(host.Engine.Workspace.ToString(), Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
        Assert.Equal("NotFound", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Cookie_writes_require_antiforgery_even_with_valid_integration_credentials()
    {
        await using var host = await Host.Create();
        host.Client.DefaultRequestHeaders.Add("Cookie", "session=human");
        var response = await host.Post("/records/query", "{\"query\":{\"limit\":1}}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Engine.Calls);
        var token = await host.Client.GetAsync("/csrf");
        host.Client.DefaultRequestHeaders.Remove("Cookie");
        host.Client.DefaultRequestHeaders.Add("Cookie", token.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        host.Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await token.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await host.Post("/records/query", "{\"query\":{\"limit\":1}}")).StatusCode);
    }

    [Fact]
    public async Task Authorization_header_presence_does_not_bypass_csrf()
    {
        await using var host = await Host.Create();
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post("/records/query", "{\"query\":{\"limit\":1}}")).StatusCode);
        Assert.Equal(0, host.Engine.Calls);
    }

    [Theory]
    [InlineData("{\"query\":{\"limit\":1,\"limit\":2}}")]
    [InlineData("{\"query\":{\"filters\":[{\"fieldId\":\"00000000-0000-0000-0000-000000000001\",\"operator\":\"Equal\",\"value\":{\"x\":1,\"x\":2}}]}}")]
    [InlineData("{\"query\":{\"limit\":1},\"subject\":\"attacker\"}")]
    [InlineData("{\"query\":null}")]
    [InlineData("{\"query\":{\"filters\":[null]}}")]
    public async Task Duplicate_or_unknown_properties_and_null_query_are_rejected(string body)
    {
        await using var host = await Host.Create();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post("/records/query", body)).StatusCode);
        Assert.Equal(0, host.Engine.Queries);
    }

    [Fact]
    public async Task Bodies_are_bounded_before_json_parsing()
    {
        await using var host = await Host.Create(configure: o => o.MaxRequestBodyBytes = 1024);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.Post("/records/query", new string(' ', 1025))).StatusCode);
        Assert.Equal(0, host.Engine.Calls);
    }

    [Fact]
    public async Task Cursors_authenticate_scope_query_schema_actor_and_reject_raw_positions()
    {
        await using var host = await Host.Create();
        var first = await host.Post("/records/query", "{\"query\":{\"limit\":1}}");
        var json = JsonNode.Parse(await first.Content.ReadAsStringAsync())!;
        var cursor = json["nextCursor"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await host.Post("/records/query", JsonSerializer.Serialize(new { query = new { limit = 1 }, cursor }))).StatusCode);
        Assert.Equal(host.Engine.Record.Id, host.Engine.LastQuery!.AfterRecordId);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post("/records/query", JsonSerializer.Serialize(new { query = new { limit = 2 }, cursor }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post("/records/query", JsonSerializer.Serialize(new { query = new { limit = 1, afterRecordId = Guid.NewGuid() } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post("/records/query", JsonSerializer.Serialize(new { query = new { limit = 1 }, cursor = cursor[..^2] + "xx" }))).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("X-Test-Actor"); host.Client.DefaultRequestHeaders.Add("X-Test-Actor", "issuer:another");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post("/records/query", JsonSerializer.Serialize(new { query = new { limit = 1 }, cursor }))).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("X-Test-Actor"); host.Client.DefaultRequestHeaders.Add("X-Test-Actor", "issuer:reader");
        host.Engine.Definition = host.Engine.Definition with { SchemaVersion = 2 };
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post("/records/query", JsonSerializer.Serialize(new { query = new { limit = 1 }, cursor }))).StatusCode);
    }

    [Fact]
    public async Task Csv_and_xlsx_are_safe_bounded_extracts_with_explicit_hidden_projection()
    {
        await using var host = await Host.Create();
        var csv = await host.Client.GetAsync(host.Path + "/export.csv?limit=1");
        Assert.Equal("true", csv.Headers.GetValues("X-CellBridge-Export-Has-More").Single());
        Assert.Equal("1", csv.Headers.GetValues("X-CellBridge-Export-Limit").Single());
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("'=SUM(1,1)", text); Assert.DoesNotContain("hidden value", text);
        var hidden = await host.Client.GetStringAsync(host.Path + "/export.csv?fields=" + host.Engine.Hidden);
        Assert.Contains("hidden value", hidden);
        var bytes = await host.Client.GetByteArrayAsync(host.Path + "/export.xlsx");
        using (var spreadsheet = SpreadsheetDocument.Open(new MemoryStream(bytes), false))
            Assert.Empty(new OpenXmlValidator().Validate(spreadsheet));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var document = XDocument.Parse(await reader.ReadToEndAsync());
        Assert.DoesNotContain(document.Descendants(), e => e.Name.LocalName == "f");
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "t" && e.Value == "=SUM(1,1)");
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "c" && e.Attribute("t")?.Value == "b");
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "c" && e.Attribute("s")?.Value == "1");
    }

    [Fact]
    public async Task Xlsx_preserves_large_numbers_early_dates_unicode_and_control_text()
    {
        await using var host = await Host.Create();
        host.Engine.Record.Values[host.Engine.Integer.ToString()] = long.MaxValue;
        host.Engine.Record.Values[host.Engine.Decimal.ToString()] = 1234567890123456.123456789012m;
        host.Engine.Record.Values[host.Engine.Date.ToString()] = "0001-01-01";
        host.Engine.Record.Values[host.Engine.Text.ToString()] = "Unicode \ud83d\ude80 control\u0001 literal_x0001_";
        var bytes = await host.Client.GetByteArrayAsync(host.Path + "/export.xlsx");
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var document = XDocument.Parse(await reader.ReadToEndAsync());
        var text = document.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value).ToArray();
        Assert.Contains(long.MaxValue.ToString(), text);
        Assert.Contains("1234567890123456.123456789012", text);
        Assert.Contains("0001-01-01", text);
        Assert.Contains("Unicode \ud83d\ude80 control_x0001_ literal_x005F_x0001_", text);
        host.Engine.Record.Values[host.Engine.Integer.ToString()] = -7;
        var csv = await host.Client.GetStringAsync(host.Path + "/export.csv");
        Assert.Contains("\"-7\"", csv);
        Assert.DoesNotContain("'-7", csv);
    }

    [Theory]
    [InlineData("{\"id\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"New\",\"fields\":null}", "")]
    [InlineData("{\"id\":\"00000000-0000-0000-0000-000000000001\",\"schemaVersion\":1,\"values\":null}", "/records")]
    [InlineData("{\"expectedVersion\":1,\"name\":\"New\",\"fields\":null}", "/schema")]
    public async Task Missing_required_collections_and_objects_are_validation_errors(string body, string suffix)
    {
        await using var host = await Host.Create();
        var path = suffix.Length == 0 ? host.Path[..host.Path.LastIndexOf('/')] : host.Path + suffix;
        var response = suffix == "/schema" ? await host.Client.PutAsync(path, new StringContent(body, Encoding.UTF8, "application/json"))
            : await host.Client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Engine.Calls);
    }

    [Fact]
    public async Task OData_metadata_is_typed_and_feed_translates_allowlisted_options()
    {
        await using var host = await Host.Create(configure: o => o.Limits = new ListLimits { MaxPageSize = 1 });
        var metadata = XDocument.Parse(await host.Client.GetStringAsync(host.Path + "/odata/$metadata"));
        var properties = metadata.Descendants().Where(e => e.Name.LocalName == "Property").ToArray();
        Assert.Contains(properties, p => p.Attribute("Name")?.Value == "f_" + host.Engine.Integer.ToString("N") && p.Attribute("Type")?.Value == "Edm.Int64");
        Assert.Contains(properties, p => p.Attribute("Type")?.Value == "Edm.Date");
        var field = "f_" + host.Engine.Integer.ToString("N");
        var response = JsonNode.Parse(await host.Client.GetStringAsync(host.Path + "/odata/Records?$filter=" + Uri.EscapeDataString(field + " ge 4") + "&$select=" + field))!;
        Assert.Equal(7, response["value"]![0]![field]!.GetValue<int>());
        Assert.Null(response["value"]![0]!["f_" + host.Engine.Text.ToString("N")]);
        Assert.Null(response["value"]![0]!["Number"]);
        Assert.Null(response["value"]![0]!["Revision"]);
        Assert.Null(response["value"]![0]!["Id"]);
        Assert.Equal(FilterOperator.GreaterOrEqual, Assert.Single(host.Engine.LastQuery!.Filters!).Operator);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(response["@odata.nextLink"]!.GetValue<string>())).StatusCode);
        Assert.NotNull(host.Engine.LastQuery!.AfterRecordId);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync(host.Path + "/odata/Records?$expand=Anything")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync(host.Path + "/odata/Records?$filter=" + Uri.EscapeDataString("contains(" + field + ",'7')"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await host.Post("/odata/Records", "{}")).StatusCode);
    }

    [Fact]
    public async Task Explicit_odata_top_limits_total_results_and_zero_returns_none()
    {
        await using var host = await Host.Create();
        var limited = JsonNode.Parse(await host.Client.GetStringAsync(host.Path + "/odata/Records?$top=1"))!;
        Assert.Single(limited["value"]!.AsArray());
        Assert.Null(limited["@odata.nextLink"]);
        Assert.Equal(1, host.Engine.LastQuery!.Limit);
        var previousQueries = host.Engine.Queries;
        var empty = JsonNode.Parse(await host.Client.GetStringAsync(host.Path + "/odata/Records?$top=0"))!;
        Assert.Empty(empty["value"]!.AsArray());
        Assert.Null(empty["@odata.nextLink"]);
        Assert.Equal(previousQueries, host.Engine.Queries);
    }

    [Fact]
    public async Task Odata_ieee754_compatibility_preserves_int64_and_decimal_values_as_strings()
    {
        await using var host = await Host.Create();
        host.Engine.Record.Values[host.Engine.Integer.ToString()] = long.MaxValue;
        host.Engine.Record.Values[host.Engine.Decimal.ToString()] = 1234567890123456.123456789012m;
        host.Client.DefaultRequestHeaders.Accept.ParseAdd("application/json;IEEE754Compatible=true");
        var response = await host.Client.GetAsync(host.Path + "/odata/Records");
        Assert.Contains(response.Content.Headers.ContentType!.Parameters, p => p.Name.Equals("IEEE754Compatible", StringComparison.OrdinalIgnoreCase) && p.Value == "true");
        var row = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["value"]![0]!;
        Assert.Equal(long.MaxValue.ToString(), row["f_" + host.Engine.Integer.ToString("N")]!.GetValue<string>());
        Assert.Equal("1234567890123456.123456789012", row["f_" + host.Engine.Decimal.ToString("N")]!.GetValue<string>());
        Assert.Equal("42", row["Number"]!.GetValue<string>());
        Assert.Equal("1", row["Revision"]!.GetValue<string>());
        host.Client.DefaultRequestHeaders.Accept.Clear();
        host.Client.DefaultRequestHeaders.Accept.ParseAdd("application/json;IEEE754Compatible=false");
        var numeric = JsonNode.Parse(await host.Client.GetStringAsync(host.Path + "/odata/Records"))!["value"]![0]!;
        Assert.Equal(long.MaxValue, numeric["f_" + host.Engine.Integer.ToString("N")]!.GetValue<long>());
        Assert.Equal(1234567890123456.123456789012m, numeric["f_" + host.Engine.Decimal.ToString("N")]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Decimal_metadata_describes_variable_scale_with_an_explicit_engine_bound()
    {
        await using var host = await Host.Create();
        host.Engine.Record.Values[host.Engine.Decimal.ToString()] = 1234567890123456789012345678m;
        ListValidation.ValidateValues(host.Engine.Definition, host.Engine.Record.Values, new ListLimits());
        var metadata = XDocument.Parse(await host.Client.GetStringAsync(host.Path + "/odata/$metadata"));
        var property = metadata.Descendants().Single(e => e.Name.LocalName == "Property" && e.Attribute("Name")?.Value == "f_" + host.Engine.Decimal.ToString("N"));
        Assert.Equal("28", property.Attribute("Precision")!.Value);
        Assert.Equal("variable", property.Attribute("Scale")!.Value);
        Assert.Contains(property.Elements(), a => a.Attribute("Term")?.Value == "CellBridge.Lists.MaximumDecimalScale" && a.Attribute("Int")?.Value == "12");
        Assert.Contains(metadata.Descendants(), e => e.Name.LocalName == "Term" && e.Attribute("Name")?.Value == "MaximumDecimalScale" && e.Attribute("Type")?.Value == "Edm.Int32");
        var row = JsonNode.Parse(await host.Client.GetStringAsync(host.Path + "/odata/Records"))!["value"]![0]!;
        Assert.Equal(1234567890123456789012345678m, row["f_" + host.Engine.Decimal.ToString("N")]!.GetValue<decimal>());
    }

    [Theory]
    [InlineData("eq", FilterOperator.Equal)]
    [InlineData("ne", FilterOperator.NotEqual)]
    public async Task Odata_reference_filters_accept_standard_unquoted_guid_literals(string comparison, FilterOperator expected)
    {
        await using var host = await Host.Create();
        const string reference = "01234567-89ab-cdef-0123-456789abcdef";
        var expression = "f_" + host.Engine.Reference.ToString("N") + " " + comparison + " " + reference;
        var response = await host.Client.GetAsync(host.Path + "/odata/Records?$filter=" + Uri.EscapeDataString(expression));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var filter = Assert.Single(host.Engine.LastQuery!.Filters!);
        Assert.Equal(expected, filter.Operator);
        Assert.Equal(reference, filter.Value!.GetValue<string>());
    }

    [Theory]
    [InlineData("eq", "01234567-89ab-cdef-0123-456789abcdeg")]
    [InlineData("eq", "0123456789abcdef0123456789abcdef")]
    [InlineData("eq", "00000000-0000-0000-0000-000000000000")]
    [InlineData("gt", "01234567-89ab-cdef-0123-456789abcdef")]
    public async Task Odata_reference_filters_reject_invalid_guids_and_order_comparisons(string comparison, string literal)
    {
        await using var host = await Host.Create();
        var expression = "f_" + host.Engine.Reference.ToString("N") + " " + comparison + " " + literal;
        var response = await host.Client.GetAsync(host.Path + "/odata/Records?$filter=" + Uri.EscapeDataString(expression));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Engine.Queries);
    }

    private sealed class Host(WebApplication app, HttpClient client, FakeEngine engine) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public FakeEngine Engine { get; } = engine;
        public string Path => $"/api/workspaces/{Engine.Workspace}/lists/{Engine.Definition.Id}";
        public Task<HttpResponseMessage> Post(string path, string body) => Client.PostAsync(Path + path, new StringContent(body, Encoding.UTF8, "application/json"));
        public IServiceProvider Services => app.Services;
        public static async Task<Host> Create(Action<CellBridgeListsOptions>? configure = null, Action<HttpContext>? requestSetup = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var engine = new FakeEngine();
            builder.Services.AddSingleton<IListEngine>(engine);
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, IntegrationHandler>("integration", _ => { });
            builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
            builder.Services.AddCellBridgeLists(o =>
            {
                o.ActorResolver = c => c.User.FindFirst("sub") is { } claim ? new ListActor(claim.Value) : null;
                o.IntegrationAuthenticationScheme = "integration";
                configure?.Invoke(o);
            });
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                requestSetup?.Invoke(context);
                if (context.Request.Headers["X-Test-Actor"].ToString() is { Length: > 0 } subject)
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "test"));
                await next(context);
            });
            app.MapGet("/csrf", (HttpContext c, IAntiforgery anti) => anti.GetAndStoreTokens(c).RequestToken!);
            app.MapCellBridgeLists();
            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Test-Actor", "issuer:reader");
            client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
            return new Host(app, client, engine);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); }
    }

    private sealed class IntegrationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.Authorization == "Bearer valid"
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Request.Headers["X-Test-Actor"].ToString())], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.Fail("Invalid token"));
    }

    private sealed class FakeEngine : IListEngine
    {
        public Guid Workspace { get; } = Guid.NewGuid();
        public Guid Text { get; } = Guid.NewGuid();
        public Guid Integer { get; } = Guid.NewGuid();
        public Guid Boolean { get; } = Guid.NewGuid();
        public Guid Decimal { get; } = Guid.NewGuid();
        public Guid Reference { get; } = Guid.NewGuid();
        public Guid Date { get; } = Guid.NewGuid();
        public Guid Hidden { get; } = Guid.NewGuid();
        public ListDefinition Definition { get; set; }
        public ListRecord Record { get; }
        public int Calls { get; private set; }
        public int Queries { get; private set; }
        public ListQuery? LastQuery { get; private set; }
        public Func<CancellationToken, Task>? QueryWait { get; set; }
        public Func<ListActor, Guid, CancellationToken, Task>? ListsWait { get; set; }
        public FakeEngine()
        {
            Definition = new ListDefinition(Guid.NewGuid(), Workspace, "Test", 1,
                [new(Text, "Text", ListFieldType.Text), new(Integer, "Integer", ListFieldType.Integer), new(Boolean, "Boolean", ListFieldType.Boolean), new(Date, "Date", ListFieldType.Date), new(Decimal, "Decimal", ListFieldType.Decimal), new(Reference, "Reference", ListFieldType.Reference, ReferenceListId: Guid.NewGuid()), new(Hidden, "Hidden", ListFieldType.Text, Display: new(Hidden: true))]);
            Record = new ListRecord(Guid.NewGuid(), Workspace, Definition.Id, 1, 1, 42,
                new JsonObject { [Text.ToString()] = "=SUM(1,1)", [Integer.ToString()] = 7, [Boolean.ToString()] = true, [Date.ToString()] = "2026-10-13", [Hidden.ToString()] = "hidden value" }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "issuer:reader", "issuer:reader");
        }
        public Task<ListDefinition> GetListAsync(ListActor actor, Guid workspaceId, Guid listId, CancellationToken ct = default)
        {
            Calls++;
            if (workspaceId != Workspace || listId != Definition.Id) throw new ListException(ListFailure.Forbidden, "secret workspace details");
            return Task.FromResult(Definition);
        }
        public async Task<RecordPage> QueryAsync(ListActor actor, Guid workspaceId, Guid listId, ListQuery query, CancellationToken ct = default)
        { Calls++; Queries++; LastQuery = query; if (QueryWait is { } wait) await wait(ct); return new RecordPage([Record], query.AfterRecordId is null ? Record.Id : null, Definition.SchemaVersion); }
        public async Task<IReadOnlyList<ListDefinition>> GetListsAsync(ListActor actor, Guid workspaceId, CancellationToken ct = default)
        { Calls++; if (ListsWait is { } wait) await wait(actor, workspaceId, ct); return [Definition]; }
        public Task<ListDefinition> CreateListAsync(ListActor actor, Guid workspaceId, CreateListRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ListDefinition> PublishSchemaAsync(ListActor actor, Guid workspaceId, Guid listId, PublishSchemaRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CreateRecordResult> CreateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, CreateRecordRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ListRecord> GetRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ListRecord> UpdateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, UpdateRecordRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, long expectedRevision, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MergePreview> PreviewMergeAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, MergeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SavedListView>> GetViewsAsync(ListActor actor, Guid workspaceId, Guid listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SavedListView> SaveViewAsync(ListActor actor, Guid workspaceId, Guid listId, SaveViewRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ReferenceLabel> ResolveReferenceAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, Guid fieldId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetMembershipAsync(ListActor actor, Guid workspaceId, string subject, bool member, bool canCreateLists, bool canManageGrants, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetListGrantAsync(ListActor actor, Guid workspaceId, Guid listId, string subject, ListAccess access, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AuditEvent>> GetAuditAsync(ListActor actor, Guid workspaceId, int limit = 100, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
