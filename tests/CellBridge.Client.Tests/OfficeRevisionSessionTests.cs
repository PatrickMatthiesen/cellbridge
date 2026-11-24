using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Client.PowerPoint;
using CellBridge.Client.Word;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Client.Tests;

public sealed class OfficeRevisionSessionTests
{
    private static readonly CellBridgeDocumentSessionOptions Writer = new()
    {
        Mode = CellBridgeDocumentSessionMode.LazyWriter,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WordRebasesAConfirmedCoherencyConflictAndRejectsAnOverlappingEdit(bool overHttp)
    {
        await using var host = await DocumentSessionScenarioTests.ScenarioHost.StartAsync(overHttp,
            enableRealTime: overHttp, documentPath: "/shared/revisions.docx");
        var head = await host.CreateAsync(WordDocumentTests.CreateBlankParagraphWithId());
        await host.SetPeerAccessAsync(head.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        await using var peerDocument = await CellBridgeDocumentSession.OpenAsync(host.PeerTransport, host.DocumentUrl, Writer);
        var peer = new WordDocumentSession(peerDocument);
        var original = peer.Revision;
        var transport = new InterceptSaves(host.Transport, async (attempt, send) =>
        {
            if (attempt == 1)
            {
                var result = await peer.ApplyAsync(new WordAppendParagraph("Peer paragraph"), original);
                Assert.Equal(OfficeWriteStatus.Success, result.Status);
            }
            return await send();
        });
        await using var document = await CellBridgeDocumentSession.OpenAsync(transport, host.DocumentUrl, Writer);
        var session = new WordDocumentSession(document);
        var paragraph = Assert.Single(session.ReadParagraphs());
        Assert.Equal(original, session.Revision);
        Assert.Empty((await host.ReadControlAsync(head.ResourceId)).Coordination.SchemaOwners);

        var saved = await session.ApplyAsync(new WordReplaceParagraph(paragraph.Reference, "Owner paragraph"), original);

        Assert.Equal(OfficeWriteStatus.Success, saved.Status);
        Assert.Equal(session.Revision, saved.Revision);
        Assert.Equal(2, transport.Proposals.Count);
        Assert.NotEqual(transport.Proposals[0].CorrelationId, transport.Proposals[1].CorrelationId);
        Assert.Equal(2, session.GetSummary().ParagraphCount);
        Assert.Equal(new[] { "Owner paragraph", "Peer paragraph" }, session.ReadParagraphs().Select(item => item.Text));
        var diff = session.ComputeDiff(original, saved.Revision, maxChanges: 1);
        Assert.Single(diff.Changes);
        Assert.True(diff.Truncated);
        Assert.Equal(2, (await host.ReadControlAsync(head.ResourceId)).Coordination.SchemaOwners.Length);
        if (overHttp)
            await AssertPersistedContentUpdateAsync(peerDocument);

        var overlap = await peer.ApplyAsync(new WordReplaceParagraph(paragraph.Reference, "Conflicting paragraph"), original);

        Assert.Equal(OfficeWriteStatus.Conflict, overlap.Status);
        Assert.Single(overlap.Conflicts);
        Assert.Equal(saved.Revision, peer.Revision);
        Assert.Equal(head.ContentVersion + 2, (await host.ReadControlAsync(head.ResourceId)).Head.ContentVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PowerPointReplaysTheSamePendingProposalWithoutAddingAnotherSlide(bool overHttp)
    {
        await using var host = await DocumentSessionScenarioTests.ScenarioHost.StartAsync(overHttp,
            documentPath: "/shared/revisions.pptx");
        var head = await host.CreateAsync(OfficeDocumentTemplates.CreatePowerPointPresentation());
        var transport = new InterceptSaves(host.Transport, async (attempt, send) =>
        {
            var response = await send();
            if (attempt <= 2)
                throw new HttpRequestException("The save response was lost.");
            return response;
        });
        await using var document = await CellBridgeDocumentSession.OpenAsync(transport, host.DocumentUrl, Writer);
        var session = new PowerPointPresentationSession(document);
        var original = session.Revision;
        var template = Assert.Single(session.ListSlides()).Id;

        var unknown = await session.ApplyAsync(new PowerPointAddSlide(template), original);

        Assert.Equal(OfficeWriteStatus.OutcomeUnknown, unknown.Status);
        Assert.True(session.HasPendingCommit);
        Assert.Equal(original, session.Revision);
        Assert.Single(session.ListSlides());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SyncAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync(new PowerPointAddSlide(), original));

        var saved = await session.RetryPendingCommitAsync();

        Assert.Equal(OfficeWriteStatus.Success, saved.Status);
        Assert.False(session.HasPendingCommit);
        Assert.Equal(3, transport.Proposals.Count);
        Assert.All(transport.Proposals, proposal =>
        {
            Assert.Equal(transport.Proposals[0].CorrelationId, proposal.CorrelationId);
            Assert.Equal(transport.Proposals[0].Payload, proposal.Payload);
        });
        Assert.Equal(saved.Revision, await session.SyncAsync());
        var slideId = Assert.IsType<uint>(saved.AddedSlideId);
        Assert.NotEqual(template, slideId);
        Assert.Equal(slideId, session.ReadSlide(slideId).Id);
        var change = Assert.Single(session.ComputeDiff(original, saved.Revision));
        Assert.Equal(slideId, change.SlideId);
        Assert.Null(change.Before);
        Assert.Equal(slideId, change.After?.Id);
        Assert.Equal(head.ContentVersion + 1, (await host.ReadControlAsync(head.ResourceId)).Head.ContentVersion);
    }

    [Fact]
    public async Task PowerPointDoesNotRebaseAnAmbiguousProposalSupersededByAPeer()
    {
        await using var host = await DocumentSessionScenarioTests.ScenarioHost.StartAsync(overHttp: false,
            documentPath: "/shared/superseded.pptx");
        var head = await host.CreateAsync(OfficeDocumentTemplates.CreatePowerPointPresentation());
        await host.SetPeerAccessAsync(head.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        await using var peerDocument = await CellBridgeDocumentSession.OpenAsync(host.PeerTransport, host.DocumentUrl, Writer);
        var peer = new PowerPointPresentationSession(peerDocument);
        var transport = new InterceptSaves(host.Transport, async (attempt, send) =>
        {
            var response = await send();
            if (attempt <= 2)
                throw new HttpRequestException("The save response was lost.");
            return response;
        });
        await using var document = await CellBridgeDocumentSession.OpenAsync(transport, host.DocumentUrl, Writer);
        var session = new PowerPointPresentationSession(document);
        var original = session.Revision;

        Assert.Equal(OfficeWriteStatus.OutcomeUnknown, (await session.ApplyAsync(new PowerPointAddSlide(), original)).Status);
        Assert.True(session.HasPendingCommit);
        var peerSave = await peer.ApplyAsync(new PowerPointAddSlide(), await peer.SyncAsync());
        Assert.Equal(OfficeWriteStatus.Success, peerSave.Status);

        var replay = await session.RetryPendingCommitAsync();

        Assert.Equal(OfficeWriteStatus.OutcomeUnknown, replay.Status);
        Assert.False(session.HasPendingCommit);
        Assert.Equal(3, transport.Proposals.Count);
        Assert.Equal(peerSave.Revision, await session.SyncAsync());
        Assert.Equal(3, session.ListSlides().Count);
        Assert.Equal(head.ContentVersion + 2, (await host.ReadControlAsync(head.ResourceId)).Head.ContentVersion);
    }

    [Fact]
    public async Task ConfirmedCoherencyConflictsStopAtTheRetryLimit()
    {
        await using var host = await DocumentSessionScenarioTests.ScenarioHost.StartAsync(overHttp: false,
            documentPath: "/shared/retry-limit.docx");
        var head = await host.CreateAsync(WordDocumentTests.CreateBlankParagraphWithId());
        await host.SetPeerAccessAsync(head.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        await using var peerDocument = await CellBridgeDocumentSession.OpenAsync(host.PeerTransport, host.DocumentUrl, Writer);
        var peer = new WordDocumentSession(peerDocument);
        var transport = new InterceptSaves(host.Transport, async (attempt, send) =>
        {
            var result = await peer.ApplyAsync(new WordAppendParagraph($"Peer paragraph {attempt}"), peer.Revision);
            Assert.Equal(OfficeWriteStatus.Success, result.Status);
            return await send();
        });
        await using var document = await CellBridgeDocumentSession.OpenAsync(transport, host.DocumentUrl, Writer);
        var session = new WordDocumentSession(document);
        var original = session.Revision;
        var paragraph = Assert.Single(session.ReadParagraphs());

        var exhausted = await session.ApplyAsync(new WordReplaceParagraph(paragraph.Reference, "Owner paragraph"), original);

        Assert.Equal(OfficeWriteStatus.RetryExhausted, exhausted.Status);
        Assert.Equal(peer.Revision, exhausted.Revision);
        Assert.False(session.HasPendingCommit);
        Assert.Equal(4, transport.Proposals.Count);
        Assert.Equal(4, session.ComputeDiff(original, exhausted.Revision).Changes.Count);
        Assert.Equal(string.Empty, session.ReadParagraphs()[0].Text);
        Assert.Equal(head.ContentVersion + 4, (await host.ReadControlAsync(head.ResourceId)).Head.ContentVersion);
    }

    [Fact]
    public async Task SessionEvictsOldRevisionsAndReturnsAnUnavailableBase()
    {
        await using var host = await DocumentSessionScenarioTests.ScenarioHost.StartAsync(overHttp: false,
            documentPath: "/shared/history.docx");
        await host.CreateAsync(OfficeDocumentTemplates.CreateWordDocument());
        await using var document = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, Writer);
        var session = new WordDocumentSession(document);
        var original = session.Revision;
        for (var index = 0; index < 8; index++)
        {
            var result = await session.ApplyAsync(new WordAppendParagraph($"Paragraph {index}"), session.Revision);
            Assert.Equal(OfficeWriteStatus.Success, result.Status);
        }

        Assert.Throws<KeyNotFoundException>(() => session.ComputeDiff(original, session.Revision));
        var stale = await session.ApplyAsync(new WordAppendParagraph("Stale edit"), original);
        Assert.Equal(OfficeWriteStatus.BaseRevisionUnavailable, stale.Status);
        Assert.Equal(session.Revision, stale.Revision);
        Assert.Equal(9, session.GetSummary().ParagraphCount);
    }

    [Fact]
    public async Task SessionChecksThePackageAndByteLimitOnConstruction()
    {
        await using var host = await DocumentSessionScenarioTests.ScenarioHost.StartAsync(overHttp: false,
            documentPath: "/shared/validation.docx");
        var package = OfficeDocumentTemplates.CreateWordDocument();
        await host.CreateAsync(package);
        await using var document = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, Writer);

        Assert.Throws<InvalidDataException>(() => new PowerPointPresentationSession(document));
        Assert.Throws<InvalidDataException>(() => new WordDocumentSession(document, package.Length - 1));
        var session = new WordDocumentSession(document, package.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            session.ApplyAsync(new WordAppendParagraph("Cannot retain the proposal"), session.Revision));
        Assert.False(session.HasPendingCommit);
        Assert.Single(session.ReadParagraphs());
    }

    private static async Task AssertPersistedContentUpdateAsync(CellBridgeDocumentSession document)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var message in document.ReadNotificationsAsync(timeout.Token))
        {
            using var notification = JsonDocument.Parse(message);
            var envelope = notification.RootElement;
            if (envelope.GetProperty("M").GetString() != "updateNotification")
                continue;
            var args = envelope.GetProperty("A");
            if (args[0].GetInt32() != 14)
                continue;
            using var payload = JsonDocument.Parse(args[1].GetString()!);
            Assert.Equal("PersistedContentUpdate", payload.RootElement.GetProperty("etts")[0].GetString());
            return;
        }
        Assert.Fail("The peer RTC connection closed before receiving the save notification.");
    }

    private sealed class InterceptSaves(ICellBridgeClientTransport inner,
        Func<int, Func<Task<CellStorageExecution>>, Task<CellStorageExecution>> intercept) : ICellBridgeClientTransport
    {
        public List<(Guid CorrelationId, byte[] Payload)> Proposals { get; } = [];

        public Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CancellationToken cancellationToken = default)
        {
            foreach (var subrequest in request.Requests.SelectMany(file => file.SubRequests))
            {
                if (subrequest.Type != SubRequestType.Cell || subrequest.SubRequestDataBinary is not { } bytes)
                    continue;
                var binary = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(bytes));
                if (!binary.SubRequests.Any(item => item.Data is PutChangesSubRequestData))
                    continue;
                Assert.Equal(CellBridgeDocumentSession.ExcelSchemaLockId, subrequest.SubRequestDataAttributes["SchemaLockID"]);
                Proposals.Add((request.CorrelationId, bytes));
                return intercept(Proposals.Count, () => inner.ExecuteAsync(request, cancellationToken));
            }
            return inner.ExecuteAsync(request, cancellationToken);
        }

        public Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
            CancellationToken cancellationToken = default) => inner.TryConnectRealTimeAsync(target, cancellationToken);
    }
}
