using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CellBridge.FssHttp.Tests;

public sealed class SecurityEndpointTests
{
    [Theory]
    [InlineData(".html")]
    [InlineData(".svg")]
    [InlineData(".txt")]
    [InlineData(".unknown")]
    public async Task NonOfficeDocumentsAreInertAttachments(string extension)
    {
        await using var fixture = await Fixture.Start();
        var bytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>");
        var state = (await fixture.Service.CreateAsync("/shared/active" + extension, bytes, TestActor.Value))!;
        using var client = fixture.Client("writer");
        using var response = await client.GetAsync(state.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData(".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    public async Task OfficeDownloadsRetainTheirProtocolMimeTypes(string extension, string expectedType)
    {
        await using var fixture = await Fixture.Start();
        var state = (await fixture.Service.CreateAsync("/shared/office" + extension, MinimalDocx.Create(), TestActor.Value))!;
        using var client = fixture.Client("writer");
        using var response = await client.GetAsync(state.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedType, response.Content.Headers.ContentType!.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task RenameCannotMakeDocumentContentExecutableInTheBrowser()
    {
        await using var fixture = await Fixture.Start();
        var state = (await fixture.Service.CreateAsync("/shared/rename.docx", MinimalDocx.Create(), TestActor.Value))!;
        var renamed = await fixture.Service.MoveDocumentAsync(state.ResourceId, "/shared/rename.html", TestActor.Value);
        using var client = fixture.Client("writer");
        using var response = await client.GetAsync(renamed.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task DeniedDocumentAndHistoryReadsDoNotRevealExistence()
    {
        await using var fixture = await Fixture.Start();
        var state = (await fixture.Service.CreateAsync("/shared/private.docx", MinimalDocx.Create(), TestActor.Value))!;
        using var client = fixture.Client("other");
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            foreach (var path in new[] { state.Path, $"/_cellbridge/history/{state.ResourceId:D}/{state.LifecycleGeneration}/1" })
            {
                using var request = new HttpRequestMessage(method, path);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Null(response.Headers.ETag);
            }
        }
    }

    [Fact]
    public async Task DiscoveryUsesFixedOriginInsteadOfUntrustedHost()
    {
        await using var fixture = await Fixture.Start(options => options.PublicOrigin = "https://office.example:8443");
        using var client = fixture.Client("writer");
        using var request = new HttpRequestMessage(HttpMethod.Options, "/shared");
        request.Headers.Host = "attacker.example";
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://office.example:8443/shared/", Assert.Single(response.Headers.GetValues("X-MSGETWEBURL")));
    }

    [Theory]
    [InlineData("*", "attacker.example", HttpStatusCode.BadRequest)]
    [InlineData("office.example", "attacker.example", HttpStatusCode.BadRequest)]
    [InlineData("office.example", "office.example", HttpStatusCode.OK)]
    [InlineData("*", "localhost", HttpStatusCode.OK)]
    public async Task UnfixedDiscoveryRequiresValidatedHost(string allowedHosts, string host, HttpStatusCode expected)
    {
        await using var fixture = await Fixture.Start(allowedHosts: allowedHosts);
        using var client = fixture.Client("writer");
        using var request = new HttpRequestMessage(HttpMethod.Options, "/shared");
        request.Headers.Host = host;
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest) Assert.False(response.Headers.Contains("X-MSGETWEBURL"));
    }

    [Fact]
    public async Task OneSubjectCannotOccupyAnotherSubjectsProtocolCapacity()
    {
        await using var fixture = await Fixture.Start(options =>
        {
            options.MaxConcurrentRequests = 2;
            options.MaxConcurrentRequestsPerSubject = 1;
            options.RequestTimeout = TimeSpan.FromSeconds(10);
        });
        using var writer = fixture.Client("writer");
        using var reader = fixture.Client("reader");
        using var third = fixture.Client("third");
        using var writerBody = fixture.BlockBody("writer");
        var writerPending = Post(writer, "writer");
        await writerBody.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var sameSubject = await Post(writer);
            Assert.Equal(HttpStatusCode.TooManyRequests, sameSubject.StatusCode);
            using var otherSubject = await Post(reader);
            Assert.Equal(HttpStatusCode.BadRequest, otherSubject.StatusCode);
            using var readerBody = fixture.BlockBody("reader");
            var readerPending = Post(reader, "reader");
            await readerBody.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                using var full = await Post(third);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
            }
            finally
            {
                readerBody.Release();
                using var completed = await readerPending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(HttpStatusCode.BadRequest, completed.StatusCode);
            }
        }
        finally
        {
            writerBody.Release();
            using var completed = await writerPending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.BadRequest, completed.StatusCode);
        }
        using var afterRelease = await Post(writer);
        Assert.Equal(HttpStatusCode.BadRequest, afterRelease.StatusCode);
    }

    [Fact]
    public async Task TimedOutBodyReadReleasesAdmissionCapacity()
    {
        await using var fixture = await Fixture.Start(options =>
        {
            options.MaxConcurrentRequests = 1;
            options.MaxConcurrentRequestsPerSubject = 1;
            options.RequestTimeout = TimeSpan.FromMilliseconds(500);
        });
        using var client = fixture.Client("writer");
        using var body = fixture.BlockBody("timeout");
        var pending = Post(client, "timeout");
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timedOut = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.RequestTimeout, timedOut.StatusCode);
        using var next = await Post(client);
        Assert.Equal(HttpStatusCode.BadRequest, next.StatusCode);
    }

    [Fact]
    public async Task SoapResponseWriteUsesTheDeadlineAndReleasesAdmissionCapacity()
    {
        await using var fixture = await Fixture.Start(options =>
        {
            options.MaxConcurrentRequests = 1;
            options.MaxConcurrentRequestsPerSubject = 1;
            options.RequestTimeout = TimeSpan.FromMilliseconds(500);
        });
        var document = (await fixture.Service.CreateAsync("/shared/tiny.docx", MinimalDocx.Create(), TestActor.Value))!;
        using var client = fixture.Client("writer");
        using var gate = fixture.BlockResponse("response");
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace protocol = CellStorageRequest.Namespace;
        var xml = new XElement(soap + "Envelope", new XElement(soap + "Body",
            new XElement(protocol + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
            new XElement(protocol + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()),
                new XElement(protocol + "Request", new XAttribute("Url", "http://localhost" + document.Path),
                    new XAttribute("RequestToken", 1), new XElement(protocol + "SubRequest",
                        new XAttribute("Type", "WhoAmI"), new XAttribute("SubRequestToken", 1))))));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_vti_bin/cellstorage.svc")
        { Content = new StringContent(xml.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml") };
        request.Headers.Add("X-Test-Blocked-Response", "response");
        var pending = client.SendAsync(request);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failure = await Record.ExceptionAsync(async () =>
            {
                using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
            });
            Assert.True(failure is null or HttpRequestException or IOException or OperationCanceledException,
                failure?.ToString());
            using var next = await Post(client);
            Assert.Equal(HttpStatusCode.BadRequest, next.StatusCode);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task AnonymousOfficeChallengeRejectsAnUntrustedHostWithoutAdvertisingLoginUrls()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "*" });
        builder.Services.AddAuthentication("office").AddCookie("office", options => options.LoginPath = "/login");
        builder.Services.AddCellBridge(new(new InMemoryStateStore(), new InMemoryContentStore()), requireDurability: false);
        builder.Services.AddCellBridgeOfficeFormsAuthentication("office");
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/shared/");
        request.Headers.Host = "attacker.example";
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("X-FORMS_BASED_AUTH_REQUIRED"));
        Assert.False(response.Headers.Contains("X-FORMS_BASED_AUTH_RETURN_URL"));
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task RequestBodyLimitRejectsAnUnknownLengthStreamAtTheBoundary()
    {
        using var body = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        await Assert.ThrowsAsync<ProtocolRequestBody.RequestBodyLimitException>(() =>
            ProtocolRequestBody.ReadAsync(body, 3, CancellationToken.None));
    }

    [Fact]
    public void ProtocolLogPathsRemoveCredentialsAndBoundUntrustedValues()
    {
        Assert.Equal("/shared/a.docx", ProtocolLogValues.Path("https://user:password@private.example/shared/a.docx?access_token=secret#fragment"));
        Assert.Equal("/shared/a.docx", ProtocolLogValues.Path("/shared/a.docx?access_token=secret#fragment"));
        Assert.Equal("<invalid-url>", ProtocolLogValues.Path("//user:password@private.example/shared/a.docx"));
        var controls = ProtocolLogValues.Path("/shared/a\r\n\t.docx");
        Assert.DoesNotContain(controls, char.IsControl);
        Assert.Equal(512, ProtocolLogValues.Path("/" + new string('a', 1024)).Length);
    }

    [Fact]
    public async Task CaptureBudgetIncludesExistingFilesAndPreservesPrivateCreation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cellbridge-security-capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "existing.bin"), new byte[] { 1, 2 });
            var store = new ProtocolCaptureStore(new() { CaptureDirectory = directory, MaxCaptureBytes = 3 },
                NullLogger<ProtocolCaptureStore>.Instance);
            await store.WriteAsync("first", ".bin", new byte[] { 3 }, CancellationToken.None);
            await store.WriteAsync("over-budget", ".bin", new byte[] { 4 }, CancellationToken.None);
            Assert.Equal(3, Directory.GetFiles(directory).Sum(path => new FileInfo(path).Length));
            Assert.False(File.Exists(Path.Combine(directory, "over-budget.bin")));
            Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(directory, "existing.bin")));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "first.bin")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string? blockedBody = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_vti_bin/cellstorage.svc")
        { Content = new StringContent("<invalid/>", Encoding.UTF8, "text/xml") };
        if (blockedBody is not null) request.Headers.Add("X-Test-Blocked-Body", blockedBody);
        return await client.SendAsync(request);
    }

    private sealed class Fixture(WebApplication app, ConcurrentDictionary<string, BlockingBody> bodies,
        ConcurrentDictionary<string, ResponseGate> responses) : IAsyncDisposable
    {
        public CellBridgeDocumentService Service => app.Services.GetRequiredService<CellBridgeDocumentService>();
        public HttpClient Client(string subject) => TestActor.CreateClient(app.GetTestServer(), subject);
        public BlockingBody BlockBody(string id)
        {
            var body = new BlockingBody();
            Assert.True(bodies.TryAdd(id, body));
            return body;
        }
        public ResponseGate BlockResponse(string id)
        {
            var gate = new ResponseGate();
            Assert.True(responses.TryAdd(id, gate));
            return gate;
        }

        public static async Task<Fixture> Start(Action<CellBridgeOptions>? configure = null, string allowedHosts = "*")
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts });
            TestActor.Register(builder.Services);
            builder.Services.AddCellBridge(new(new InMemoryStateStore(), new InMemoryContentStore()),
                requireDurability: false, configure: configure);
            var app = builder.Build();
            var bodies = new ConcurrentDictionary<string, BlockingBody>();
            var responses = new ConcurrentDictionary<string, ResponseGate>();
            app.Use(async (context, next) =>
            {
                if (bodies.TryGetValue(context.Request.Headers["X-Test-Blocked-Body"].ToString(), out var body))
                    context.Request.Body = body;
                if (responses.TryGetValue(context.Request.Headers["X-Test-Blocked-Response"].ToString(), out var gate))
                    context.Response.Body = new BlockingResponse(context.Response.Body, gate);
                await next(context);
            });
            app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge();
            await app.StartAsync();
            return new(app, bodies, responses);
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private sealed class ResponseGate : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public async Task WaitAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            try { await _release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
        public void Dispose() => Release();
    }

    private sealed class BlockingResponse(Stream underlying, ResponseGate gate) : Stream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            await underlying.WriteAsync(buffer, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            await underlying.FlushAsync(cancellationToken);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
    }

    private sealed class BlockingBody : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Release(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
    }
}
