using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using System.Collections;
using CellBridge.Lists;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.Lists.AspNetCore;

public static class CellBridgeListsExtensions
{
    public static IServiceCollection AddCellBridgeLists(this IServiceCollection services, Action<CellBridgeListsOptions> configure)
    {
        var options = new CellBridgeListsOptions();
        configure(options);
        options.Limits.Validate();
        if (options.MaxRequestBodyBytes is < 1024 or > 16 * 1024 * 1024 || options.Limits.MaxPageSize is < 1 or > 10000
            || options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(configure));
        services.AddSingleton(options);
        services.AddDataProtection();
        services.AddAntiforgery();
        return services;
    }

    public static IEndpointRouteBuilder MapCellBridgeLists(this IEndpointRouteBuilder endpoints,
        string prefix = "/api/workspaces/{workspaceId:guid}/lists")
    {
        var group = endpoints.MapGroup(prefix);
        Map(group, "GET", "", async s => Results.Json(await s.Engine.GetListsAsync(s.Actor, s.Workspace, s.Ct), HttpState.Json));
        Map(group, "POST", "", async s => Results.Json(await s.Engine.CreateListAsync(s.Actor, s.Workspace, await s.Read<CreateListRequest>(), s.Ct), HttpState.Json, statusCode: 201));
        Map(group, "GET", "/{listId:guid}", async s => Results.Json(await s.Definition(), HttpState.Json));
        Map(group, "PUT", "/{listId:guid}/schema", async s => Results.Json(await s.Engine.PublishSchemaAsync(s.Actor, s.Workspace, s.List, await s.Read<PublishSchemaRequest>(), s.Ct), HttpState.Json));
        Map(group, "POST", "/{listId:guid}/records", async s => Results.Json(await s.Engine.CreateRecordAsync(s.Actor, s.Workspace, s.List, await s.Read<CreateRecordRequest>(), s.Ct), HttpState.Json, statusCode: 201));
        Map(group, "GET", "/{listId:guid}/records/{recordId:guid}", async s => Results.Json(await s.Engine.GetRecordAsync(s.Actor, s.Workspace, s.List, s.Record, s.Ct), HttpState.Json));
        Map(group, "PATCH", "/{listId:guid}/records/{recordId:guid}", async s => Results.Json(await s.Engine.UpdateRecordAsync(s.Actor, s.Workspace, s.List, s.Record, await s.Read<UpdateRecordRequest>(), s.Ct), HttpState.Json));
        Map(group, "DELETE", "/{listId:guid}/records/{recordId:guid}", async s =>
        {
            s.CheckQuery("expectedRevision");
            if (!long.TryParse(s.SingleQuery("expectedRevision"), out var revision) || revision < 1) throw HttpState.Invalid();
            await s.Engine.DeleteRecordAsync(s.Actor, s.Workspace, s.List, s.Record, revision, s.Ct);
            return Results.NoContent();
        });
        Map(group, "POST", "/{listId:guid}/records/query", async s =>
        {
            var body = await s.Read<ListQueryRequest>();
            var definition = await s.Definition();
            var query = s.Query(body.Query, body.Cursor, definition);
            var page = await s.Engine.QueryAsync(s.Actor, s.Workspace, s.List, query, s.Ct);
            s.CheckSchema(definition, page);
            return Results.Json(new ListQueryResponse(page.Records, page.SchemaVersion, s.Cursor(body.Query, definition, page.NextRecordId)), HttpState.Json);
        });
        Map(group, "POST", "/{listId:guid}/records/{recordId:guid}/merge-preview", async s => Results.Json(await s.Engine.PreviewMergeAsync(s.Actor, s.Workspace, s.List, s.Record, await s.Read<MergeRequest>(), s.Ct), HttpState.Json));
        Map(group, "GET", "/{listId:guid}/views", async s => Results.Json(await s.Engine.GetViewsAsync(s.Actor, s.Workspace, s.List, s.Ct), HttpState.Json));
        Map(group, "PUT", "/{listId:guid}/views", async s =>
        {
            var body = await s.Read<SaveViewRequest>();
            if (body.Query is null || body.Query.AfterRecordId is not null) throw HttpState.Invalid();
            return Results.Json(await s.Engine.SaveViewAsync(s.Actor, s.Workspace, s.List, body, s.Ct), HttpState.Json);
        });
        Map(group, "GET", "/{listId:guid}/records/{recordId:guid}/references/{fieldId:guid}", async s => Results.Json(await s.Engine.ResolveReferenceAsync(s.Actor, s.Workspace, s.List, s.Record, s.Route("fieldId"), s.Ct), HttpState.Json));
        Map(group, "PUT", "/{listId:guid}/grants", async s =>
        {
            var body = await s.Read<ListGrantRequest>();
            await s.Engine.SetListGrantAsync(s.Actor, s.Workspace, s.List, body.Subject, body.Access, s.Ct);
            return Results.NoContent();
        });
        Map(group, "GET", "/{listId:guid}/export.csv", s => ListExtract.Export(s, false));
        Map(group, "GET", "/{listId:guid}/export.xlsx", s => ListExtract.Export(s, true));
        Map(group, "GET", "/{listId:guid}/odata", ODataFeed.Service);
        Map(group, "GET", "/{listId:guid}/odata/$metadata", ODataFeed.Metadata);
        Map(group, "GET", "/{listId:guid}/odata/Records", ODataFeed.Records);
        return endpoints;
    }

    private static void Map(RouteGroupBuilder group, string method, string path, Func<HttpState, Task<IResult>> action)
    {
        group.MapMethods(path, [method], (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var options = context.RequestServices.GetRequiredService<CellBridgeListsOptions>();
            return (IResult)new DeadlineListResult(options, async () =>
            {
                try
                {
                    context.RequestAborted.ThrowIfCancellationRequested();
                    if (context.User.Identity?.IsAuthenticated != true || options.ActorResolver is null)
                        return Results.StatusCode(401);
                    var actor = options.ActorResolver(context);
                    if (actor is null || string.IsNullOrWhiteSpace(actor.Subject)) return Results.StatusCode(401);
                    if (method != "GET")
                    {
                        if (context.Request.ContentLength > options.MaxRequestBodyBytes)
                            throw new ListException(ListFailure.Limit, "Body limit exceeded.");
                        // Antiforgery may parse form bodies. This API accepts JSON only,
                        // so reject forms before invoking antiforgery's parser.
                        if (context.Request.HasFormContentType || method != "DELETE" && !context.Request.HasJsonContentType())
                            throw HttpState.Invalid();
                        var integration = false;
                        if (!context.Request.Headers.ContainsKey("Cookie") && options.IntegrationAuthenticationScheme is { } scheme)
                        {
                            var auth = await context.AuthenticateAsync(scheme);
                            integration = auth.Succeeded && auth.Principal?.Identity?.IsAuthenticated == true
                                && ResolveIntegrationActor(context, auth.Principal, options) == actor;
                        }
                        if (!integration) await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                    }
                    return await action(new HttpState(context, actor, options));
                }
                catch (AntiforgeryValidationException) { return Results.Json(new { error = "antiforgery" }, statusCode: 400); }
                catch (ListException e)
                {
                    var code = e.Failure switch { ListFailure.Validation => 400, ListFailure.NotFound => 404, ListFailure.Forbidden => 404,
                        ListFailure.Conflict => 409, ListFailure.Limit => 413, _ => 503 };
                    var error = e.Failure is ListFailure.Forbidden or ListFailure.NotFound ? "NotFound" : e.Failure.ToString();
                    return Results.Json(new { error }, statusCode: code);
                }
                catch (JsonException) { return Results.Json(new { error = "Validation" }, statusCode: 400); }
                catch (BadHttpRequestException) { return Results.StatusCode(400); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
                catch (Exception) { return Results.Json(new { error = "Unavailable" }, statusCode: 503); }
            });
        });
    }

    private static ListActor? ResolveIntegrationActor(HttpContext context, System.Security.Claims.ClaimsPrincipal principal, CellBridgeListsOptions options)
    {
        var original = context.User;
        try { context.User = principal; return options.ActorResolver!(context); }
        finally { context.User = original; }
    }

}

internal sealed class DeadlineListResult(CellBridgeListsOptions options, Func<Task<IResult>> action) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        var originalAbort = context.RequestAborted;
        var originalResponse = context.Features.Get<IHttpResponseBodyFeature>()!;
        var deadline = new CancellationTokenSource(options.RequestTimeout);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(originalAbort, deadline.Token);
        context.RequestAborted = cancellation.Token;
        // Some framework results flush BodyWriter without passing RequestAborted.
        // Apply the deadline at the stream boundary as well.
        context.Response.Body = new DeadlineResponseStream(originalResponse.Stream, cancellation.Token);
        // Cancellation alone does not bound an uncooperative stream or response
        // writer. Abort the transport at the absolute deadline, even after headers.
        var abort = deadline.Token.Register(context.Abort);
        try { await ExecuteResponse(context); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { context.Abort(); }
        finally
        {
            // Hosts must honor cancellation. If custom code does not, abort still
            // bounds the client connection, while awaiting it keeps request-scoped
            // dependencies alive until the work ends.
            abort.Dispose();
            cancellation.Dispose();
            deadline.Dispose();
            context.RequestAborted = originalAbort;
            context.Features.Set(originalResponse);
        }
    }

    private async Task ExecuteResponse(HttpContext context)
    {
        var result = await action();
        context.RequestAborted.ThrowIfCancellationRequested();
        await result.ExecuteAsync(context);
    }
}

internal sealed class DeadlineResponseStream(Stream inner, CancellationToken requestCancellation) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { requestCancellation.ThrowIfCancellationRequested(); inner.Flush(); }
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        await inner.FlushAsync(linked?.Token ?? requestCancellation);
    }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count)
    { requestCancellation.ThrowIfCancellationRequested(); inner.Write(buffer, offset, count); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = Link(cancellationToken);
        await inner.WriteAsync(buffer, linked?.Token ?? requestCancellation);
    }
    private CancellationTokenSource? Link(CancellationToken supplied) => supplied.CanBeCanceled && supplied != requestCancellation
        ? CancellationTokenSource.CreateLinkedTokenSource(supplied, requestCancellation) : null;
    // The host owns the underlying response stream.
    protected override void Dispose(bool disposing) { }
}

internal sealed class HttpState(HttpContext context, ListActor actor, CellBridgeListsOptions options)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter() }
    };
    public HttpContext Context { get; } = context;
    public ListActor Actor { get; } = actor;
    public CellBridgeListsOptions Options { get; } = options;
    public IListEngine Engine => Context.RequestServices.GetRequiredService<IListEngine>();
    public CancellationToken Ct => Context.RequestAborted;
    public Guid Workspace => Route("workspaceId");
    public Guid List => Route("listId");
    public Guid Record => Route("recordId");
    public Guid Route(string name) => Guid.Parse(Context.Request.RouteValues[name]!.ToString()!);
    public Task<ListDefinition> Definition() => Engine.GetListAsync(Actor, Workspace, List, Ct);
    public static ListException Invalid() => new(ListFailure.Validation, "Invalid request.");

    public async Task<T> Read<T>()
    {
        if (Context.Request.ContentLength > Options.MaxRequestBodyBytes) throw new ListException(ListFailure.Limit, "Body limit exceeded.");
        if (!Context.Request.HasJsonContentType()) throw Invalid();
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await Context.Request.Body.ReadAsync(bytes, Ct)) > 0)
        {
            if (buffer.Length + read > Options.MaxRequestBodyBytes) throw new ListException(ListFailure.Limit, "Body limit exceeded.");
            buffer.Write(bytes, 0, read);
        }
        using var doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicates(doc.RootElement);
        var result = doc.RootElement.Deserialize<T>(Json) ?? throw Invalid();
        ValidateEnvelope(result, new NullabilityInfoContext());
        return result;
    }

    private void ValidateEnvelope(object? value, NullabilityInfoContext nullability)
    {
        if (value is null) throw Invalid();
        if (value is System.Text.Json.Nodes.JsonNode node)
        {
            if (Encoding.UTF8.GetByteCount(node.ToJsonString()) > Options.Limits.MaxRecordBytes)
                throw new ListException(ListFailure.Limit, "Record body limit exceeded.");
            return;
        }
        if (value is string || value.GetType().IsValueType) return;
        if (value is IEnumerable items)
        {
            foreach (var item in items) ValidateEnvelope(item, nullability);
            return;
        }
        foreach (var property in value.GetType().GetProperties())
        {
            var item = property.GetValue(value);
            if (item is null)
            {
                if (nullability.Create(property).ReadState == NullabilityState.NotNull) throw Invalid();
            }
            else ValidateEnvelope(item, nullability);
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!keys.Add(property.Name)) throw Invalid(); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    public void CheckQuery(params string[] allowed)
    {
        if (Context.Request.Query.Any(x => !allowed.Contains(x.Key, StringComparer.Ordinal) || x.Value.Count != 1)) throw Invalid();
    }
    public string? SingleQuery(string name)
    {
        var value = Context.Request.Query[name];
        if (value.Count > 1 || (value.Count == 1 && value[0]!.Length > 8192)) throw Invalid();
        return value.Count == 0 ? null : value[0];
    }
    public void CheckSchema(ListDefinition definition, RecordPage page)
    { if (definition.SchemaVersion != page.SchemaVersion) throw new ListException(ListFailure.Conflict, "Schema changed."); }

    public ListQuery Query(ListQuery query, string? cursor, ListDefinition definition, string purpose = "native")
    {
        if (query is null || query.AfterRecordId is not null || query.Limit < 1 || query.Limit > Options.Limits.MaxPageSize) throw Invalid();
        ListValidation.ValidateQuery(definition, query, Options.Limits);
        if (cursor is null) return query;
        if (cursor.Length > 8192) throw Invalid();
        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(Protector.Unprotect(cursor), Json) ?? throw Invalid();
            if (payload.Workspace != Workspace || payload.List != List || payload.Schema != definition.SchemaVersion
                || payload.Actor != Actor.Subject || payload.Digest != Digest(query, purpose) || payload.Expires < DateTimeOffset.UtcNow) throw Invalid();
            return query with { AfterRecordId = payload.After };
        }
        catch (CryptographicException) { throw Invalid(); }
    }
    public string? Cursor(ListQuery query, ListDefinition definition, Guid? next, string purpose = "native") => next is null ? null
        : Protector.Protect(JsonSerializer.Serialize(new CursorPayload(Workspace, List, definition.SchemaVersion, Actor.Subject,
            Digest(query, purpose), next.Value, DateTimeOffset.UtcNow.AddHours(1)), Json));
    private IDataProtector Protector => Context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("CellBridge.Lists.Cursor.v1");
    private static string Digest(ListQuery query, string purpose) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "\n" + JsonSerializer.Serialize(query, Json))));
    private sealed record CursorPayload(Guid Workspace, Guid List, long Schema, string Actor, string Digest, Guid After, DateTimeOffset Expires);
}
