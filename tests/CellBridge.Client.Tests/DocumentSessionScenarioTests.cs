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
    public async Task InvalidSaveResponseReplaysTheProposalAndNextSaveUsesAppliedIndex(bool overHttp)
    {
        await using var host = await ScenarioHost.StartAsync(overHttp);
        var unchanged = new byte[2 * 1024 * 1024];
        new Random(37).NextBytes(unchanged);
        var original = AddEntry(OfficeDocumentTemplates.CreateExcelWorkbook(), "custom/unchanged.bin", unchanged);
        var firstEdit = AddEntry(original, "custom/first.txt", "first"u8);
        var sheet = Assert.Single(ExcelWorkbook.ListSheets(firstEdit)).SheetId;
        var secondEdit = Assert.IsType<byte[]>(ExcelWorkbook.MergeCells(firstEdit, firstEdit, sheet,
            [new("A1", ExcelCellValue.String(new string('x', 2000)))]).Package);
        var originalOffset = firstEdit.AsSpan().IndexOf(unchanged.AsSpan(0, 32));
        var shiftedOffset = secondEdit.AsSpan().IndexOf(unchanged.AsSpan(0, 32));
        Assert.True(originalOffset >= 0 && shiftedOffset > originalOffset);
        var document = await host.CreateAsync(original);
        var transport = new InvalidateFirstSaveResponse(host.Transport);
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
            "The save should omit both shifted, unchanged 1 MiB chunks.");
        var latest = await host.ReadControlAsync(document.ResourceId);
        Assert.Equal(document.ContentVersion + 2, latest.Head.ContentVersion);

        transport.FileQueryPayloadSizes.Clear();
        Assert.Equal(secondEdit, (await session.SyncAsync()).Package);
        Assert.True(Assert.Single(transport.FileQueryPayloadSizes) < FsshttpdChunker.DefaultChunkSize,
            "Sync should use knowledge of both saved and retained data elements.");

        await using var reopened = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl,
            new CellBridgeDocumentSessionOptions { QueryPageBytes = 256 * 1024 });
        Assert.Equal(secondEdit, reopened.GetSnapshot().Package);
    }

    [Theory]
    [InlineData(false, 1, "lost")]
    [InlineData(true, 2, "lost")]
    [InlineData(true, 1, "duplicate reply")]
    [InlineData(true, 1, "duplicate index")]
    [InlineData(true, 1, "overflow")]
    [InlineData(true, 1, "SOAP fault")]
    public async Task SupersededSaveWithAnAmbiguousResponseDoesNotRebase(bool overHttp, int lostReplies,
        string failure)
    {
        await using var host = await ScenarioHost.StartAsync(overHttp);
        var original = OfficeDocumentTemplates.CreateExcelWorkbook();
        var document = await host.CreateAsync(original);
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        var options = new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter };
        await using var peer = await CellBridgeDocumentSession.OpenAsync(host.PeerTransport, host.DocumentUrl, options);
        var transport = new InterceptSaveResponses(host.Transport, async (attempt, send) =>
        {
            var response = await send();
            if (attempt > lostReplies)
                return response;
            if (attempt == lostReplies)
            {
                var accepted = await peer.SyncAsync();
                Assert.NotEqual(original, accepted.Package);
                Assert.True((await peer.TryCommitAsync(accepted.Revision, original)).Succeeded);
            }
            if (failure == "duplicate reply")
            {
                var subresponses = Assert.Single(response.Response.Responses).SubResponses;
                subresponses.Add(Assert.Single(subresponses));
                return response;
            }
            if (failure == "duplicate index")
            {
                var subresponse = Assert.Single(Assert.Single(response.Response.Responses).SubResponses);
                var binary = Assert.IsType<FsshttpbResponse>(subresponse.BinaryResponse);
                var put = Assert.IsType<PutChangesSubResponseData>(Assert.Single(binary.SubResponses).Data);
                var elements = binary.DataElementPackage!.DataElements;
                elements.Add(Assert.Single(elements, element =>
                    element.DataElementExtendedGuid.Equals(put.PutChangesResponse!.AppliedStorageIndexID)));
                return response;
            }
            if (failure == "overflow")
                throw new OverflowException("The save response had an invalid payload size.");
            if (failure == "SOAP fault")
            {
                response.Response.Fault = new("Server", "The server could not return the save response.", "<Fault />");
                return response;
            }
            throw new HttpRequestException("The save response was lost.");
        });
        await using var session = await CellBridgeDocumentSession.OpenAsync(transport, host.DocumentUrl, options);
        var workbook = new ExcelWorkbookSession(session);
        var sheet = Assert.Single(workbook.ListSheets()).SheetId;
        var originalRead = workbook.ReadRangeAtRevision(sheet, "A1");
        Task<ExcelWriteResult> EditAsync() =>
            workbook.SetCellsAsync(sheet, [new("A1", ExcelCellValue.Number(42))], originalRead.Revision);

        ExcelWriteResult result;
        if (lostReplies == 2)
        {
            await Assert.ThrowsAsync<IOException>(EditAsync);
            result = await workbook.RetryPendingCommitAsync();
        }
        else
        {
            result = await EditAsync();
        }
        Assert.Equal(ExcelWriteStatus.OutcomeUnknown, result.Status);
        Assert.Equal(lostReplies + 1, transport.Payloads.Count);
        Assert.All(transport.Payloads, payload => Assert.Equal(transport.Payloads[0], payload));
        Assert.Equal(document.ContentVersion + 2, (await host.ReadControlAsync(document.ResourceId)).Head.ContentVersion);

        Assert.Equal(peer.GetSnapshot().Revision, await workbook.SyncAsync());
        Assert.Equal(original, session.GetSnapshot().Package);
        Assert.Equal(Assert.Single(originalRead.Cells).Value, Assert.Single(workbook.ReadRange(sheet, "A1")).Value);
    }

    [Fact]
    public async Task FirstHttp413ClearsRejectedProposal()
    {
        await using var host = await ScenarioHost.StartAsync(overHttp: true, maxRequestBytes: 128 * 1024);
        var original = OfficeDocumentTemplates.CreateExcelWorkbook();
        var document = await host.CreateAsync(original);
        await using var session = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl,
            new() { Mode = CellBridgeDocumentSessionMode.EagerWriter });
        var initial = session.GetSnapshot();
        var oversized = AddEntry(original, "custom/large.bin", new byte[256 * 1024]);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => session.TryCommitAsync(initial.Revision, oversized));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, error.StatusCode);
        Assert.Equal(document.ContentVersion, (await host.ReadControlAsync(document.ResourceId)).Head.ContentVersion);
        Assert.Equal(initial.Revision, (await session.SyncAsync()).Revision);
        var smaller = AddEntry(original, "custom/small.txt", "small"u8);
        Assert.True((await session.TryCommitAsync(initial.Revision, smaller)).Succeeded);
    }

    [Fact]
    public async Task Http413AfterAnAmbiguousResponsePreservesThePendingProposal()
    {
        await using var host = await ScenarioHost.StartAsync(overHttp: false);
        var original = OfficeDocumentTemplates.CreateExcelWorkbook();
        var changed = AddEntry(original, "custom/saved.txt", "saved"u8);
        var document = await host.CreateAsync(original);
        var transport = new InterceptSaveResponses(host.Transport, async (attempt, send) =>
        {
            if (attempt == 2)
                throw new HttpRequestException("Request rejected before execution.", null, HttpStatusCode.RequestEntityTooLarge);
            var response = await send();
            if (attempt == 1)
                throw new HttpRequestException("The response timed out.", null, HttpStatusCode.RequestTimeout);
            return response;
        });
        await using var session = await CellBridgeDocumentSession.OpenAsync(transport, host.DocumentUrl,
            new() { Mode = CellBridgeDocumentSessionMode.EagerWriter });
        var revision = session.GetSnapshot().Revision;
        var error = await Assert.ThrowsAsync<IOException>(() => session.TryCommitAsync(revision, changed));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, Assert.IsType<HttpRequestException>(error.InnerException).StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SyncAsync());
        Assert.True((await session.TryCommitAsync(revision, changed)).Succeeded);
        Assert.Equal(3, transport.Payloads.Count);
        Assert.All(transport.Payloads, payload => Assert.Equal(transport.Payloads[0], payload));
        Assert.Equal(document.ContentVersion + 1, (await host.ReadControlAsync(document.ResourceId)).Head.ContentVersion);
    }

    [Fact]
    public async Task WriterRejoinsAfterTheServerRejectsItsReleasedSession()
    {
        await using var host = await ScenarioHost.StartAsync(overHttp: false);
        var original = OfficeDocumentTemplates.CreateExcelWorkbook();
        var document = await host.CreateAsync(original);
        await using var session = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl,
            new() { Mode = CellBridgeDocumentSessionMode.EagerWriter });
        var released = await host.Transport.ExecuteAsync(new()
        {
            Requests =
            {
                new()
                {
                    RequestToken = 1,
                    Url = host.DocumentUrl.AbsoluteUri,
                    ResourceId = document.ResourceId.ToString("D"),
                    UseResourceId = true,
                    SubRequests =
                    [
                        new()
                        {
                            SubRequestToken = 1,
                            Type = SubRequestType.Coauth,
                            SubRequestDataAttributes =
                            {
                                ["CoauthRequestType"] = "ExitCoauthoring",
                                ["ClientID"] = session.ClientId.ToString("B"),
                                ["SchemaLockID"] = CellBridgeDocumentSession.ExcelSchemaLockId,
                            },
                        },
                    ],
                },
            },
        });
        Assert.Equal("Success", Assert.Single(Assert.Single(released.Response.Responses).SubResponses).ErrorCode);
        var revision = session.GetSnapshot().Revision;
        var changed = AddEntry(original, "custom/saved.txt", "saved"u8);
        var error = await Assert.ThrowsAsync<CellBridgeProtocolException>(() => session.TryCommitAsync(revision, changed));
        Assert.Equal("InvalidCoauthSession", error.ErrorCode);
        Assert.True((await session.TryCommitAsync(revision, changed)).Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealTimeDiscoveryTimeoutAllowsOpenUnlessCallerCancelled(bool cancel)
    {
        await using var host = await ScenarioHost.StartAsync(overHttp: false);
        var original = OfficeDocumentTemplates.CreateExcelWorkbook();
        var document = await host.CreateAsync(original);
        using var cancellation = new CancellationTokenSource();
        var transport = new RememberRealTimeConnection(host.Transport, () =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw new TimeoutException("RTC discovery timed out.");
        });

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CellBridgeDocumentSession.OpenAsync(
                transport, host.DocumentUrl, cancellationToken: cancellation.Token));
            Assert.Empty((await host.ReadControlAsync(document.ResourceId)).Editors);
        }
        else
        {
            await using var reader = await CellBridgeDocumentSession.OpenAsync(transport, host.DocumentUrl);
            Assert.Equal(original, reader.GetSnapshot().Package);
            Assert.Null(transport.Target);
            Assert.Single((await host.ReadControlAsync(document.ResourceId)).Editors);
        }
    }

    [Fact]
    public async Task DuplicateRealTimePropertyAllowsDocumentSyncWithoutRealTime()
    {
        await using var host = await ScenarioHost.StartAsync(overHttp: true, enableRealTime: true);
        var original = OfficeDocumentTemplates.CreateExcelWorkbook();
        await host.CreateAsync(original);
        var transport = new RememberRealTimeConnection(host.Transport, rewriteProperties: _ => """
            <PropertyValues>
              <PropertyValue id="RealtimeTypingEndpointUrl" value="https://example.test/rtc"/>
              <PropertyValue id="DocumentAccessToken" value="first"/>
              <PropertyValue id="DocumentAccessToken" value="second"/>
            </PropertyValues>
            """);

        await using var reader = await CellBridgeDocumentSession.OpenAsync(transport, host.DocumentUrl);

        Assert.Equal(original, reader.GetSnapshot().Package);
        Assert.Null(transport.Target);
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

    private sealed class InvalidateFirstSaveResponse(ICellBridgeClientTransport inner) : ICellBridgeClientTransport
    {
        public List<SaveExchange> Saves { get; } = [];
        public List<long> FileQueryPayloadSizes { get; } = [];

        public async Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CancellationToken cancellationToken = default)
        {
            var result = await inner.ExecuteAsync(request, cancellationToken);
            foreach (var file in request.Requests)
            foreach (var sub in file.SubRequests.Where(item => item.Type == SubRequestType.Cell))
            {
                var bytes = sub.SubRequestDataBinary
                    ?? Convert.FromBase64String(Assert.IsType<string>(sub.SubRequestDataXml));
                var binary = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(bytes));
                var response = result.Response.Responses.Single(item => item.RequestToken == file.RequestToken);
                var subResponse = response.SubResponses.Single(item => item.SubRequestToken == sub.SubRequestToken);
                var decoded = subResponse.BinaryResponse ?? FsshttpbResponse.FromBytes(subResponse.SubResponseDataBase64!);
                var put = binary.SubRequests.Select(item => item.Data).OfType<PutChangesSubRequestData>().SingleOrDefault();
                if (put is null)
                {
                    if (Guid.Parse(sub.SubRequestDataAttributes["PartitionID"]) == Guid.Empty)
                        FileQueryPayloadSizes.Add(decoded.DataElementPackage?.DataElements.Sum(element =>
                            (long)element.GetSerializedLength()) ?? 0);
                    continue;
                }

                Assert.True(file.UseResourceId);
                Assert.True(Guid.TryParse(file.ResourceId, out _));
                Assert.True(Guid.TryParse(sub.SubRequestDataAttributes["ClientID"], out _));
                Assert.Equal(Guid.Parse("29358EC1-E813-4793-8E70-ED0344E7B73C"),
                    Guid.Parse(sub.SubRequestDataAttributes["SchemaLockID"]));
                Assert.False(put.ExpectedStorageIndex.IsNull);
                Assert.Contains(binary.DataElementPackage!.DataElements, element =>
                    element.DataElementType == DataElementType.StorageIndexDataElementData &&
                    element.DataElementExtendedGuid.Equals(put.ExpectedStorageIndex));

                var putResponse = Assert.IsType<PutChangesSubResponseData>(Assert.Single(decoded.SubResponses).Data);
                var appliedIndex = putResponse.PutChangesResponse!.AppliedStorageIndexID;
                Assert.False(appliedIndex.IsNull);
                Assert.NotEmpty(putResponse.SerialNumberReassignAllBytes!);
                Saves.Add(new(bytes, put, appliedIndex));
                if (Saves.Count == 1)
                    subResponse.SubResponseStreamInvalid = true;
            }
            return result;
        }

        public Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
            CancellationToken cancellationToken = default) => inner.TryConnectRealTimeAsync(target, cancellationToken);
    }

    private sealed record SaveExchange(byte[] Bytes, PutChangesSubRequestData Proposal, ExGuid AppliedIndex);

    private sealed class InterceptSaveResponses(ICellBridgeClientTransport inner,
        Func<int, Func<Task<CellStorageExecution>>, Task<CellStorageExecution>> intercept) : ICellBridgeClientTransport
    {
        public List<byte[]> Payloads { get; } = [];

        public Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CancellationToken cancellationToken = default)
        {
            foreach (var sub in request.Requests.SelectMany(file => file.SubRequests).Where(item => item.Type == SubRequestType.Cell))
            {
                var bytes = Assert.IsType<byte[]>(sub.SubRequestDataBinary);
                var binary = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(bytes));
                if (!binary.SubRequests.Any(item => item.Data is PutChangesSubRequestData))
                    continue;
                Payloads.Add(bytes);
                return intercept(Payloads.Count, () => inner.ExecuteAsync(request, cancellationToken));
            }
            return inner.ExecuteAsync(request, cancellationToken);
        }

        public Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
            CancellationToken cancellationToken = default) => inner.TryConnectRealTimeAsync(target, cancellationToken);
    }

    private sealed class RememberRealTimeConnection(ICellBridgeClientTransport inner, Action? discover = null,
        Func<string?, string>? rewriteProperties = null)
        : ICellBridgeClientTransport
    {
        public RealTimeConnectionTarget? Target { get; private set; }
        public ICellBridgeRealTimeChannel? Channel { get; private set; }

        public async Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CancellationToken cancellationToken = default)
        {
            var isDiscovery = request.Requests.Any(file => file.SubRequests.Any(sub => sub.Type == SubRequestType.Properties));
            if (isDiscovery)
                discover?.Invoke();
            var execution = await inner.ExecuteAsync(request, cancellationToken);
            if (isDiscovery && rewriteProperties is not null)
            {
                var response = Assert.Single(Assert.Single(execution.Response.Responses).SubResponses);
                response.SubResponseDataXml = rewriteProperties(response.SubResponseDataXml);
            }
            return execution;
        }

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

        public static async Task<ScenarioHost> StartAsync(bool overHttp, bool enableRealTime = false, long? maxRequestBytes = null)
        {
            var content = new InMemoryContentStore();
            var storage = new StorageProvider(new InMemoryDocumentRepository(content), content);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddAuthentication();
            builder.Services.AddCellBridge(storage, requireDurability: false,
                configure: options =>
                {
                    options.EnableOfficeRealTime = enableRealTime;
                    if (maxRequestBytes is { } limit)
                        options.MaxRequestBytes = limit;
                });
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
