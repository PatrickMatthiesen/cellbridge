using System.Collections;
using System.Net;
using System.Reflection;
using CellBridge.Lists.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.Lists.AspNetCore.Tests;

public sealed partial class HttpTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public void Partition_limits_require_bounded_positive_configuration(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCellBridgeLists(o => o.MaxConcurrentRequestsPerActor = limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCellBridgeLists(o => o.MaxConcurrentRequestsPerWorkspace = limit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void Request_deadline_requires_finite_positive_configuration(int seconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCellBridgeLists(o => o.RequestTimeout = TimeSpan.FromSeconds(seconds)));

    [Fact]
    public async Task Actor_cap_cannot_be_bypassed_by_changing_workspaces_and_other_actor_is_served()
    {
        await using var host = await Host.Create(configure: o => o.MaxConcurrentRequestsPerActor = 2);
        var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Engine.ListsWait = async (actor, _, ct) =>
        {
            if (actor.Subject != "issuer:reader") return;
            entered.Release();
            await release.Task.WaitAsync(ct);
        };
        var held = Enumerable.Range(0, 2).Select(_ => GetWorkspace(host, Guid.NewGuid(), "issuer:reader")).ToArray();
        await entered.WaitAsync(TimeSpan.FromSeconds(5));
        await entered.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GetWorkspace(host, Guid.NewGuid(), "issuer:reader")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetWorkspace(host, Guid.NewGuid(), "issuer:other")).StatusCode);
        }
        finally { release.TrySetResult(); }
        foreach (var response in await Task.WhenAll(held)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertPartitionsEmpty(host);
    }

    [Fact]
    public async Task Workspace_cap_applies_across_actors_while_another_workspace_is_served()
    {
        await using var host = await Host.Create(configure: o => o.MaxConcurrentRequestsPerWorkspace = 2);
        var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = Guid.NewGuid();
        host.Engine.ListsWait = async (_, id, ct) =>
        {
            if (id != workspace) return;
            entered.Release();
            await release.Task.WaitAsync(ct);
        };
        var held = Enumerable.Range(0, 2).Select(i => GetWorkspace(host, workspace, "issuer:" + i)).ToArray();
        await entered.WaitAsync(TimeSpan.FromSeconds(5));
        await entered.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GetWorkspace(host, workspace, "issuer:third")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetWorkspace(host, Guid.NewGuid(), "issuer:third")).StatusCode);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(held);
        AssertPartitionsEmpty(host);
    }

    [Fact]
    public async Task Deadline_aborts_chunked_body_without_entering_engine_and_releases_admission()
    {
        using var body = new HeldStream(read: true);
        await using var host = await Host.Create(configure: o => { o.RequestTimeout = TimeSpan.FromMilliseconds(200); o.MaxConcurrentRequests = 1; },
            requestSetup: context =>
            {
                if (context.Request.Headers.ContainsKey("X-Slow-Body"))
                {
                    context.Request.ContentLength = null;
                    context.Request.Body = body;
                }
            });
        using var request = new HttpRequestMessage(HttpMethod.Post, host.Path + "/records/query")
        { Content = new StringContent("{\"query\":{\"limit\":1}}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Slow-Body", "true");
        var response = host.Client.SendAsync(request);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertAborted(response);
        await body.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, host.Engine.Calls);
        await AssertReusable(host);
    }

    [Fact]
    public async Task Deadline_cancels_backend_and_releases_admission()
    {
        await using var host = await Host.Create(configure: o => { o.RequestTimeout = TimeSpan.FromMilliseconds(200); o.MaxConcurrentRequests = 1; });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Engine.QueryWait = async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cancelled.SetResult(); }
        };
        var response = host.Post("/records/query", "{\"query\":{\"limit\":1}}");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertAborted(response);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Engine.QueryWait = null;
        await AssertReusable(host);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_aborts_response_write_before_or_after_headers(bool started)
    {
        using var responseBody = new HeldStream(read: false);
        var headersAtAbort = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await Host.Create(configure: o => { o.RequestTimeout = TimeSpan.FromMilliseconds(200); o.MaxConcurrentRequests = 1; },
            requestSetup: context =>
            {
                if (context.Request.Headers.ContainsKey("X-Slow-Response"))
                {
                    var original = context.Features.Get<IHttpResponseBodyFeature>()!;
                    if (started) responseBody.BeforeHold = original.StartAsync;
                    context.Response.Body = responseBody;
                    context.RequestAborted.Register(() => headersAtAbort.TrySetResult(context.Response.HasStarted));
                }
            });
        using var request = new HttpRequestMessage(HttpMethod.Get, host.Path + "/odata/$metadata");
        request.Headers.Add("X-Slow-Response", started ? "started" : "not-started");
        // JSON/text result serialization exercises the real adapter response execution.
        var response = host.Client.SendAsync(request);
        await responseBody.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertAborted(response);
        await responseBody.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(started, await headersAtAbort.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await AssertReusable(host);
    }

    [Fact]
    public async Task Noncooperative_backend_keeps_lease_and_request_scope_until_work_finishes()
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await Host.Create(configure: o => { o.RequestTimeout = TimeSpan.FromMilliseconds(200); o.MaxConcurrentRequestsPerActor = 1; },
            requestSetup: context =>
            {
                if (context.Request.Headers["X-Test-Actor"] == "issuer:reader")
                    context.RequestAborted.Register(() => aborted.TrySetResult());
            });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Engine.ListsWait = async (actor, _, _) =>
        {
            if (actor.Subject != "issuer:reader") return;
            entered.SetResult();
            await release.Task;
        };
        var held = GetWorkspace(host, Guid.NewGuid(), "issuer:reader");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GetWorkspace(host, Guid.NewGuid(), "issuer:reader")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetWorkspace(host, Guid.NewGuid(), "issuer:other")).StatusCode);
        }
        finally { release.TrySetResult(); }
        await AssertAborted(held);
        host.Engine.ListsWait = null;
        await AssertReusable(host);
    }

    private static async Task<HttpResponseMessage> GetWorkspace(Host host, Guid workspace, string actor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workspaces/{workspace}/lists");
        request.Headers.Add("X-Test-Actor", actor);
        return await host.Client.SendAsync(request);
    }

    private static async Task AssertAborted(Task<HttpResponseMessage> request)
    {
        var failure = await Record.ExceptionAsync(async () => await request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(failure);
        Assert.IsNotType<TimeoutException>(failure);
        Assert.True(failure is OperationCanceledException or HttpRequestException or IOException, failure.ToString());
    }

    private static async Task AssertReusable(Host host)
    {
        HttpStatusCode status = HttpStatusCode.ServiceUnavailable;
        for (var attempt = 0; attempt < 50 && status == HttpStatusCode.ServiceUnavailable; attempt++)
        {
            status = (await host.Client.GetAsync(host.Path)).StatusCode;
            if (status == HttpStatusCode.ServiceUnavailable) await Task.Delay(10);
        }
        Assert.Equal(HttpStatusCode.OK, status);
        AssertPartitionsEmpty(host);
    }

    private static void AssertPartitionsEmpty(Host host)
    {
        // Inspect only active admission bookkeeping, not transport state.
        var type = typeof(CellBridgeListsOptions).Assembly.GetType("CellBridge.Lists.AspNetCore.ListRequestGate")!;
        var gate = host.Services.GetRequiredService(type);
        foreach (var name in new[] { "actors", "workspaces" })
            Assert.Empty((IDictionary)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate)!);
    }

    private sealed class HeldStream(bool read) : Stream
    {
        public Func<CancellationToken, Task>? BeforeHold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => read;
        public override bool CanSeek => false;
        public override bool CanWrite => !read;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private async Task Hold(CancellationToken cancellation)
        {
            if (BeforeHold is { } before) await before(cancellation);
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); }
            finally { Cancelled.TrySetResult(); }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Hold(cancellationToken); return 0; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await Hold(cancellationToken); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Hold(cancellationToken);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
