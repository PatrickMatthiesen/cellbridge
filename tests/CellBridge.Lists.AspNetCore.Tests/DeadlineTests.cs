using System.Net;
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
    [InlineData(3601)]
    public void Request_deadline_requires_finite_positive_configuration(int seconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCellBridgeLists(o => o.RequestTimeout = TimeSpan.FromSeconds(seconds)));

    [Fact]
    public async Task Parallel_requests_for_same_actor_and_workspace_are_not_rejected_by_library()
    {
        await using var host = await Host.Create();
        var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Engine.ListsWait = async (_, _, ct) =>
        {
            entered.Release();
            await release.Task.WaitAsync(ct);
        };
        var held = Enumerable.Range(0, 16).Select(_ => GetWorkspace(host, host.Engine.Workspace, "issuer:reader")).ToArray();
        try
        {
            for (var request = 0; request < held.Length; request++)
                Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)), "Every parallel request must reach the engine.");
            foreach (var suffix in new[] { "", "/export.csv", "/export.xlsx", "/odata/$metadata", "/odata/Records" })
            {
                var response = await host.Client.GetAsync(host.Path + suffix);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            }
        }
        finally { release.TrySetResult(); }
        foreach (var response in await Task.WhenAll(held).WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Deadline_aborts_chunked_body_before_engine_call_and_other_requests_still_succeed()
    {
        using var body = new HeldStream(read: true);
        await using var host = await Host.Create(configure: o => o.RequestTimeout = TimeSpan.FromMilliseconds(200),
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
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.Path)).StatusCode);
    }

    [Fact]
    public async Task Deadline_cancels_backend_without_affecting_other_requests()
    {
        await using var host = await Host.Create(configure: o => o.RequestTimeout = TimeSpan.FromMilliseconds(200));
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
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.Path)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_aborts_response_write_before_or_after_headers(bool started)
    {
        using var responseBody = new HeldStream(read: false);
        var headersAtAbort = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await Host.Create(configure: o => o.RequestTimeout = TimeSpan.FromMilliseconds(200),
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
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.Path)).StatusCode);
    }

    [Fact]
    public async Task Noncooperative_backend_keeps_request_scope_alive_after_transport_abort_until_work_finishes()
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolvedScope = new TaskCompletionSource<RequestScope>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await Host.Create(configure: o => o.RequestTimeout = TimeSpan.FromMilliseconds(200),
            requestSetup: context =>
            {
                if (context.Request.Headers.ContainsKey("X-Held-Request"))
                {
                    resolvedScope.TrySetResult(context.RequestServices.GetRequiredService<RequestScope>());
                    context.RequestAborted.Register(() => aborted.TrySetResult());
                }
            }, configureServices: services => services.AddScoped<RequestScope>());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        host.Engine.ListsWait = async (_, _, _) =>
        {
            if (Interlocked.Increment(ref first) != 1) return;
            entered.SetResult();
            await release.Task;
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workspaces/{host.Engine.Workspace}/lists");
        request.Headers.Add("X-Held-Request", "true");
        var held = host.Client.SendAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var scope = await resolvedScope.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(scope.Disposed.Task.IsCompleted);
            Assert.Equal(HttpStatusCode.OK, (await GetWorkspace(host, host.Engine.Workspace, "issuer:reader")).StatusCode);
        }
        finally { release.TrySetResult(); }
        await AssertAborted(held);
        await scope.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Engine.ListsWait = null;
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.Path)).StatusCode);
    }

    private sealed class RequestScope : IDisposable
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Disposed.TrySetResult();
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
