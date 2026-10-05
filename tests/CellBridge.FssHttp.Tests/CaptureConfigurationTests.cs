using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CellBridge.FssHttp.Tests;

public sealed class CaptureConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("enabled")]
    public async Task SoapCaptureConfigurationPreservesDiagnosticsWithoutLoggingDocumentBytes(string? setting)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cellbridge-capture-" + Guid.NewGuid().ToString("N"));
        var enabled = setting == "enabled";
        var logs = new RecordedLogs();
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            TestActor.Register(builder.Services);
            var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
            builder.Services.AddCellBridge(provider, requireDurability: false,
                configure: options => options.CaptureDirectory = enabled ? directory : setting);
            await using var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge();
            const string marker = "private-document-marker-4629";
            var state = (await app.Services.GetRequiredService<CellBridgeDocumentService>()
                .CreateAsync("/capture.txt", Encoding.UTF8.GetBytes(marker), TestActor.Value))!;
            await app.StartAsync();
            var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges)
                { RequestId = 1, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } } };
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            XNamespace protocol = CellStorageRequest.Namespace;
            var xml = new XElement(soap + "Envelope", new XElement(soap + "Body",
                new XElement(protocol + "RequestCollection", new XElement(protocol + "Request",
                    new XAttribute("Url", "http://localhost" + state.Path), new XAttribute("RequestToken", 1),
                    new XElement(protocol + "SubRequest", new XAttribute("Type", "Cell"), new XAttribute("SubRequestToken", 1),
                        new XElement(protocol + "SubRequestData", Convert.ToBase64String(query.ToByteArray())))))))
                .ToString(SaveOptions.DisableFormatting);
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/_vti_bin/cellstorage.svc")
                { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
            request.Headers.Add("X-Test-User", "writer");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            var envelope = Encoding.UTF8.GetString(responseBytes);
            var binary = Convert.FromBase64String(XDocument.Parse(envelope).Descendants(protocol + "SubResponseData").Single().Value);
            Assert.False(Assert.Single(FsshttpbResponse.Deserialize(new BinaryReaderEx(binary)).SubResponses).Status);
            var diagnostics = string.Join('\n', logs.Messages);
            Assert.Contains("FSSHTTP request received", diagnostics);
            Assert.Contains("Cell response token=1", diagnostics);
            Assert.Contains("FSSHTTP response id=", diagnostics);
            Assert.DoesNotContain(marker, diagnostics);
            Assert.DoesNotContain(Convert.ToBase64String(binary), diagnostics);
            Assert.DoesNotContain(xml, diagnostics);
            Assert.DoesNotContain(envelope, diagnostics);
            if (enabled)
            {
                var captured = Assert.Single(Directory.GetFiles(directory, "*.response.bin"));
                var id = Path.GetFileName(captured).Split('.')[0];
                Assert.Equal(responseBytes, await File.ReadAllBytesAsync(captured));
                Assert.Equal(xml, await File.ReadAllTextAsync(Path.Combine(directory, id + ".request.bin")));
                Assert.Equal("text/xml; charset=utf-8", await File.ReadAllTextAsync(Path.Combine(directory, id + ".response.content-type.txt")));
                Assert.True(File.Exists(Path.Combine(directory, id + ".request.content-type.txt")));
                Assert.True(File.Exists(Path.Combine(directory, id + ".summary.json")));
                Assert.Contains("FSSHTTP response id=" + id, diagnostics);
                Assert.Equal(5, Directory.GetFiles(directory).Length);
            }
            else
            {
                Assert.Null(app.Services.GetRequiredService<CellBridgeOptions>().CaptureDirectory);
                Assert.False(Directory.Exists(directory));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Recorder(Messages);
        public void Dispose() { }

        private sealed class Recorder(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
