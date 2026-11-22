using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using CellBridge.AspNetCore;
using CellBridge.Client.Excel;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CellBridge.Client.Tests;

public sealed class DocumentSessionScenarioTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderTracksSavesWithoutAcquiringASchemaLock(bool overHttp)
    {
        await using var host = await ScenarioHost.StartAsync(overHttp);
        var original = OfficeDocumentTemplates.CreateExcelWorkbook();
        var changed = AddEntry(original, "custom/change.txt", "saved by writer"u8);
        var document = await host.CreateAsync(original);

        await using (var reader = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl))
        {
            var initial = reader.GetSnapshot();
            Assert.Equal(original, initial.Package);
            var reading = await host.ReadControlAsync(document.ResourceId);
            Assert.Single(reading.Editors);
            Assert.Empty(reading.Coordination.SchemaOwners);

            await using (var writer = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl,
                new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter }))
            {
                var writing = await host.ReadControlAsync(document.ResourceId);
                Assert.Equal(2, writing.Editors.Length);
                Assert.Single(writing.Coordination.SchemaOwners);

                Assert.True((await writer.TryCommitAsync(writer.GetSnapshot().Revision, changed)).Succeeded);
                Assert.Equal(changed, writer.GetSnapshot().Package);
                await reader.SyncAsync();
                Assert.NotEqual(initial.Revision, reader.GetSnapshot().Revision);
                Assert.Equal(changed, reader.GetSnapshot().Package);
            }

            var remaining = await host.ReadControlAsync(document.ResourceId);
            Assert.Single(remaining.Editors);
            Assert.Empty(remaining.Coordination.SchemaOwners);
        }
        Assert.Empty((await host.ReadControlAsync(document.ResourceId)).Editors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctExcelCoauthorsMergeDisjointEditsDetectOverlapAndHonorRevocation(bool overHttp)
    {
        await using var host = await ScenarioHost.StartAsync(overHttp);
        var document = await host.CreateAsync(OfficeDocumentTemplates.CreateExcelWorkbook());
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        var options = new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter };
        await using var first = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options);
        await using var second = await CellBridgeDocumentSession.OpenAsync(host.PeerTransport, host.DocumentUrl, options);
        var firstWorkbook = new ExcelWorkbookSession(first);
        var secondWorkbook = new ExcelWorkbookSession(second);
        var sheet = Assert.Single(firstWorkbook.ListSheets()).SheetId;
        var firstRead = firstWorkbook.ReadRangeAtRevision(sheet, "A1:B1");
        var secondRead = secondWorkbook.ReadRangeAtRevision(sheet, "A1:B1");
        Assert.Equal(firstRead.Revision, secondRead.Revision);
        var control = await host.ReadControlAsync(document.ResourceId);
        Assert.Equal(new[] { "client-tests:owner", "client-tests:peer" },
            control.Coordination.SchemaOwners.Select(lease => lease.OwnerSubject).Order(StringComparer.Ordinal));

        var disjoint = await Task.WhenAll(
            firstWorkbook.SetCellsAsync(sheet, [new("A1", ExcelCellValue.Number(10))], firstRead.Revision),
            secondWorkbook.SetCellsAsync(sheet, [new("B1", ExcelCellValue.Number(20))], secondRead.Revision));
        Assert.All(disjoint, result => Assert.Equal(ExcelWriteStatus.Success, result.Status));
        var shared = await firstWorkbook.SyncAsync();
        Assert.Equal(shared, await secondWorkbook.SyncAsync());
        Assert.Equal(new[] { "10", "20" }, secondWorkbook.ReadRange(sheet, "A1:B1").Select(cell => cell.Value.RawValue));

        var overwrite = await firstWorkbook.SetCellsAsync(sheet, [new("A1", ExcelCellValue.Number(30))], shared);
        Assert.Equal(ExcelWriteStatus.Success, overwrite.Status);
        var overlap = await secondWorkbook.SetCellsAsync(sheet, [new("A1", ExcelCellValue.Number(40))], shared);
        Assert.Equal(ExcelWriteStatus.CellConflict, overlap.Status);
        Assert.Equal(new ExcelCellConflict("A1", ExcelCellValue.Number(10), ExcelCellValue.Number(30), ExcelCellValue.Number(40)),
            Assert.Single(overlap.Conflicts));

        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read);
        var newest = await secondWorkbook.SyncAsync();
        Assert.Equal(overwrite.Revision, newest);
        var denied = await Assert.ThrowsAsync<CellBridgeProtocolException>(() =>
            secondWorkbook.SetCellsAsync(sheet, [new("B1", ExcelCellValue.Number(40))], newest));
        Assert.Equal($"HResult {CellBridgeAuthorization.AccessDeniedHResult}", denied.ErrorCode);
        Assert.Equal(overwrite.Revision, await firstWorkbook.SyncAsync());
        Assert.Equal(new[] { "30", "20" }, firstWorkbook.ReadRange(sheet, "A1:B1").Select(cell => cell.Value.RawValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentWritersRejectAStalePackageAndCanCommitAfterSync(bool overHttp)
    {
        await using var host = await ScenarioHost.StartAsync(overHttp);
        var original = OfficeDocumentTemplates.CreateExcelWorkbook();
        var document = await host.CreateAsync(original);
        var firstEdit = AddEntry(original, "custom/first.txt", "first"u8);
        var staleEdit = AddEntry(original, "custom/second.txt", "second"u8);
        var merged = AddEntry(firstEdit, "custom/second.txt", "second"u8);
        var options = new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter };
        await using var first = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options);
        await using var second = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options);

        var joined = await host.ReadControlAsync(document.ResourceId);
        Assert.Equal(2, joined.Coordination.SchemaOwners.Length);
        Assert.Equal(2, joined.Editors.Select(editor => editor.ClientId).Distinct().Count());

        var initial = second.GetSnapshot().Revision;
        Assert.Equal(initial, first.GetSnapshot().Revision);
        Assert.True((await first.TryCommitAsync(initial, firstEdit)).Succeeded);
        Assert.Equal(CellBridgeCommitStatus.Conflict, (await second.TryCommitAsync(initial, staleEdit)).Status);

        await second.SyncAsync();
        Assert.Equal(firstEdit, second.GetSnapshot().Package);
        Assert.True((await second.TryCommitAsync(second.GetSnapshot().Revision, merged)).Succeeded);
        await first.SyncAsync();
        Assert.Equal(merged, first.GetSnapshot().Package);
        Assert.Equal(document.ContentVersion + 2, (await host.ReadControlAsync(document.ResourceId)).Head.ContentVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostSaveResponseReplaysTheProposalAndNextSaveUsesAppliedIndex(bool overHttp)
    {
        await using var host = await ScenarioHost.StartAsync(overHttp);
        var unchanged = new byte[2 * 1024 * 1024];
        new Random(37).NextBytes(unchanged);
        var original = AddEntry(OfficeDocumentTemplates.CreateExcelWorkbook(), "custom/unchanged.bin", unchanged);
        var firstEdit = AddEntry(original, "custom/first.txt", "first"u8);
        var secondEdit = AddEntry(firstEdit, "custom/second.txt", "second"u8);
        var document = await host.CreateAsync(original);
        var transport = new LoseFirstSaveResponse(host.Transport);
        await using var session = await CellBridgeDocumentSession.OpenAsync(transport, host.DocumentUrl,
            new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter });

        Assert.True((await session.TryCommitAsync(session.GetSnapshot().Revision, firstEdit)).Succeeded);
        Assert.Equal(firstEdit, session.GetSnapshot().Package);
        Assert.Equal(2, transport.Saves.Count);
        Assert.Equal(transport.Saves[0].Bytes, transport.Saves[1].Bytes);
        Assert.Equal(transport.Saves[0].AppliedIndex, transport.Saves[1].AppliedIndex);
        var afterRetry = await host.ReadControlAsync(document.ResourceId);
        Assert.Equal(document.ContentVersion + 1, afterRetry.Head.ContentVersion);
        Assert.Equal(document.Counters.SaveReceipts + 1, afterRetry.Head.Counters.SaveReceipts);

        Assert.True((await session.TryCommitAsync(session.GetSnapshot().Revision, secondEdit)).Succeeded);
        Assert.Equal(secondEdit, session.GetSnapshot().Package);
        Assert.Equal(3, transport.Saves.Count);
        var next = transport.Saves[2];
        Assert.Equal(transport.Saves[1].AppliedIndex, next.Proposal.ExpectedStorageIndex);
        Assert.NotEqual(next.Proposal.StorageIndex, next.AppliedIndex);
        Assert.True(next.Bytes.Length < FsshttpdChunker.DefaultChunkSize,
            "The save should omit both unchanged 1 MiB chunks.");
        var latest = await host.ReadControlAsync(document.ResourceId);
        Assert.Equal(document.ContentVersion + 2, latest.Head.ContentVersion);

        await using var reopened = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl,
            new CellBridgeDocumentSessionOptions { QueryPageBytes = 256 * 1024 });
        Assert.Equal(secondEdit, reopened.GetSnapshot().Package);
    }

    [Fact]
    public async Task ReaderConnectsToRtcWithItsEditorClientIdAndCanUpdateTheTicket()
    {
        await using var host = await ScenarioHost.StartAsync(overHttp: true, enableRealTime: true);
        var document = await host.CreateAsync(OfficeDocumentTemplates.CreateExcelWorkbook());
        var transport = new RememberRealTimeConnection(host.Transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reader = await CellBridgeDocumentSession.OpenAsync(transport, host.DocumentUrl,
            cancellationToken: timeout.Token);

        var target = Assert.IsType<RealTimeConnectionTarget>(transport.Target);
        var channel = Assert.IsAssignableFrom<ICellBridgeRealTimeChannel>(transport.Channel);
        var control = await host.ReadControlAsync(document.ResourceId);
        var editor = Assert.Single(control.Editors);
        Assert.Equal(editor.ClientId, Guid.Parse(target.ClientId));
        await channel.UpdateClientInfoAsync(target, timeout.Token);
    }

    private static byte[] AddEntry(byte[] package, string name, ReadOnlySpan<byte> content)
    {
        using var stream = new MemoryStream();
        stream.Write(package);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        using (var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open())
            entry.Write(content);
        return stream.ToArray();
    }

    private sealed class LoseFirstSaveResponse(ICellBridgeClientTransport inner) : ICellBridgeClientTransport
    {
        public List<SaveExchange> Saves { get; } = [];

        public async Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CancellationToken cancellationToken = default)
        {
            var result = await inner.ExecuteAsync(request, cancellationToken);
            foreach (var file in request.Requests)
            foreach (var sub in file.SubRequests.Where(item => item.Type == SubRequestType.Cell))
            {
                var bytes = sub.SubRequestDataBinary
                    ?? Convert.FromBase64String(Assert.IsType<string>(sub.SubRequestDataXml));
                var binary = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(bytes));
                var put = binary.SubRequests.Select(item => item.Data).OfType<PutChangesSubRequestData>().SingleOrDefault();
                if (put is null)
                    continue;

                Assert.True(file.UseResourceId);
                Assert.True(Guid.TryParse(file.ResourceId, out _));
                Assert.True(Guid.TryParse(sub.SubRequestDataAttributes["ClientID"], out _));
                Assert.Equal(Guid.Parse("29358EC1-E813-4793-8E70-ED0344E7B73C"),
                    Guid.Parse(sub.SubRequestDataAttributes["SchemaLockID"]));
                Assert.False(put.ExpectedStorageIndex.IsNull);
                Assert.Contains(binary.DataElementPackage!.DataElements, element =>
                    element.DataElementType == DataElementType.StorageIndexDataElementData &&
                    element.DataElementExtendedGuid.Equals(put.ExpectedStorageIndex));

                var response = result.Response.Responses.Single(item => item.RequestToken == file.RequestToken);
                var subResponse = response.SubResponses.Single(item => item.SubRequestToken == sub.SubRequestToken);
                var decoded = FsshttpbResponse.Deserialize(new BinaryReaderEx(subResponse.SubResponseDataBase64!));
                var putResponse = Assert.IsType<PutChangesSubResponseData>(Assert.Single(decoded.SubResponses).Data);
                var appliedIndex = putResponse.PutChangesResponse!.AppliedStorageIndexID;
                Assert.False(appliedIndex.IsNull);
                Assert.NotEmpty(putResponse.SerialNumberReassignAllBytes!);
                Saves.Add(new(bytes, put, appliedIndex));
                if (Saves.Count == 1)
                    throw new HttpRequestException("The host committed the save, but the response was lost.");
            }
            return result;
        }

        public Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
            CancellationToken cancellationToken = default) => inner.TryConnectRealTimeAsync(target, cancellationToken);
    }

    private sealed record SaveExchange(byte[] Bytes, PutChangesSubRequestData Proposal, ExGuid AppliedIndex);

    private sealed class RememberRealTimeConnection(ICellBridgeClientTransport inner) : ICellBridgeClientTransport
    {
        public RealTimeConnectionTarget? Target { get; private set; }
        public ICellBridgeRealTimeChannel? Channel { get; private set; }

        public Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CancellationToken cancellationToken = default) =>
            inner.ExecuteAsync(request, cancellationToken);

        public async Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
            CancellationToken cancellationToken = default)
        {
            Target = target;
            Channel = await inner.TryConnectRealTimeAsync(target, cancellationToken);
            return Channel;
        }
    }

    private sealed class ScenarioHost : IAsyncDisposable
    {
        private const string Path = "/shared/client-scenario.xlsx";
        private const string PeerHeader = "X-CellBridge-Test-Peer";
        private static readonly CellBridgeActor Actor = new(new("client-tests:owner", "scenario-owner", "Scenario owner"),
            CanCreate: true);
        private static readonly CellBridgeActor Peer = new(new("client-tests:peer", "scenario-peer", "Scenario peer"));
        private readonly WebApplication app;
        private readonly StorageProvider storage;
        private readonly HttpClient? http;
        private readonly HttpClient? peerHttp;

        private ScenarioHost(WebApplication app, StorageProvider storage, Uri origin, HttpClient? http)
        {
            this.app = app;
            this.storage = storage;
            this.http = http;
            DocumentUrl = new Uri(origin, Path);
            if (http is null)
            {
                var processor = app.Services.GetRequiredService<CellBridgeRequestProcessor>();
                var authority = origin.GetLeftPart(UriPartial.Authority);
                Transport = new InProcessCellBridgeTransport(processor, authority, Actor);
                PeerTransport = new InProcessCellBridgeTransport(processor, authority, Peer);
            }
            else
            {
                peerHttp = new HttpClient { BaseAddress = origin, Timeout = http.Timeout };
                peerHttp.DefaultRequestHeaders.Add(PeerHeader, "true");
                Transport = new HttpCellBridgeTransport(http, origin);
                PeerTransport = new HttpCellBridgeTransport(peerHttp, origin);
            }
        }

        public Uri DocumentUrl { get; }
        public ICellBridgeClientTransport Transport { get; }
        public ICellBridgeClientTransport PeerTransport { get; }

        public static async Task<ScenarioHost> StartAsync(bool overHttp, bool enableRealTime = false)
        {
            var content = new InMemoryContentStore();
            var storage = new StorageProvider(new InMemoryDocumentRepository(content), content);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddAuthentication();
            builder.Services.AddCellBridge(storage, requireDurability: false,
                configure: options => options.EnableOfficeRealTime = enableRealTime);
            var app = builder.Build();
            try
            {
                if (!overHttp)
                    return new ScenarioHost(app, storage, new Uri("http://localhost"), http: null);

                app.Use((context, next) =>
                {
                    var actor = context.Request.Headers.ContainsKey(PeerHeader) ? Peer : Actor;
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(CellBridgeActor.SubjectClaim, actor.Identity.Subject),
                        new Claim(ClaimTypes.Name, actor.Identity.Login),
                        new Claim(CellBridgeActor.DisplayNameClaim, actor.Identity.DisplayName),
                    ], "client-test"));
                    return next(context);
                });
                app.UseAuthorization();
                app.MapCellBridge();
                await app.StartAsync();
                var server = app.Services.GetRequiredService<IServer>();
                var origin = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                return new ScenarioHost(app, storage, origin, new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(10) });
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public async Task<DocumentHead> CreateAsync(byte[] bytes) =>
            Assert.IsType<DocumentHead>(await app.Services.GetRequiredService<CellBridgeDocumentService>().CreateAsync(Path, bytes, Actor));

        public async Task<DocumentControlRead> ReadControlAsync(Guid resourceId) =>
            Assert.IsType<DocumentControlRead>(await storage.Documents.ReadControlAsync(resourceId));

        public async Task SetPeerAccessAsync(Guid resourceId, DocumentAccess access)
        {
            var head = (await ReadControlAsync(resourceId)).Head;
            var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
            var status = await service.SetRecipientGrantAsync(resourceId, head.LifecycleGeneration,
                head.AuthorityGeneration, Actor, Peer.Identity, access);
            Assert.Equal(DocumentSharingUpdateStatus.Applied, status);
        }

        public async ValueTask DisposeAsync()
        {
            http?.Dispose();
            peerHttp?.Dispose();
            await app.DisposeAsync();
        }
    }
}
