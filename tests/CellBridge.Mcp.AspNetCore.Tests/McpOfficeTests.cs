using System.Net;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.Tests;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using static CellBridge.Mcp.AspNetCore.Tests.McpWorkbookTests;

namespace CellBridge.Mcp.AspNetCore.Tests;

public sealed class McpOfficeTests
{
    private const string WordPath = "/shared/agenda.docx";
    private const string PresentationPath = "/shared/briefing.pptx";

    [Fact]
    public async Task WordToolsReadPageEditDiffAndCloseOverHttp()
    {
        await using var host = await McpTestHost.StartAsync(configure: options => options.MaxReadCells = 1);
        var document = await host.CreateOfficeDocumentAsync(WordPath, WordPackage());
        using var http = host.CreateHttp("owner");
        var mcp = new McpHttpSession(http);
        await mcp.InitializeAsync();

        var opened = await SuccessAsync(mcp, "open_word_document", new { path = WordPath });
        var documentHandle = opened.GetProperty("documentHandle").GetString();
        var initialRevision = Revision(opened);
        Assert.Equal(document.ResourceId, opened.GetProperty("resourceId").GetGuid());
        Assert.Equal(documentHandle, (await SuccessAsync(mcp, "open_word_document", new { resourceId = document.ResourceId }))
            .GetProperty("documentHandle").GetString());
        var summary = await SuccessAsync(mcp, "get_word_summary", new { documentHandle });
        Assert.Equal(initialRevision, Revision(summary));
        Assert.Equal(2, summary.GetProperty("paragraphCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("headingCount").GetInt32());
        var headings = await SuccessAsync(mcp, "list_word_headings", new { documentHandle });
        Assert.Equal("Agenda", Text(Assert.Single(headings.GetProperty("headings").EnumerateArray())));
        var first = await SuccessAsync(mcp, "read_word_paragraphs", new { documentHandle });
        Assert.Equal(initialRevision, Revision(first));
        Assert.Equal("Agenda", Text(Assert.Single(first.GetProperty("paragraphs").EnumerateArray())));
        var second = await SuccessAsync(mcp, "read_word_paragraphs",
            new { documentHandle, startIndex = first.GetProperty("nextIndex").GetInt32() });
        var paragraph = Assert.Single(second.GetProperty("paragraphs").EnumerateArray());
        Assert.Equal("Draft", Text(paragraph));
        var target = paragraph.GetProperty("reference");
        Assert.False(second.TryGetProperty("nextIndex", out var next) && next.ValueKind == JsonValueKind.Number);
        await ErrorAsync(mcp, "read_word_paragraphs", new { documentHandle, maxParagraphs = 2 });
        await ErrorAsync(mcp, "append_word_paragraph",
            new { documentHandle, text = new string('é', 32_769), baseRevision = initialRevision, operationId = "oversized" });
        Assert.Contains("operationId", await ErrorAsync(mcp, "append_word_paragraph",
            new { documentHandle, text = "Ignored", baseRevision = initialRevision, operationId = " " }));
        await ErrorAsync(mcp, "delete_word_paragraph", new { documentHandle, target = new { }, baseRevision = initialRevision });

        var editors = await SuccessAsync(mcp, "get_word_editors", new { documentHandle });
        Assert.Equal("owner", Assert.Single(editors.GetProperty("editors").EnumerateArray()).GetProperty("loginName").GetString());
        await AssertLazyAsync(host, document.ResourceId);
        var replaced = await SuccessAsync(mcp, "replace_word_paragraph",
            new { documentHandle, target, text = "Approved", baseRevision = initialRevision });
        AssertWriteStatus(replaced, "Success");
        await AssertWriterAsync(host, document.ResourceId);
        var conflict = await SuccessAsync(mcp, "replace_word_paragraph",
            new { documentHandle, target, text = "Rejected", baseRevision = initialRevision });
        AssertWriteStatus(conflict, "Conflict");
        Assert.Single(conflict.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(Revision(replaced), Revision(conflict));
        var diff = await SuccessAsync(mcp, "diff_word_document",
            new { documentHandle, beforeRevision = initialRevision, afterRevision = Revision(replaced) });
        var change = Assert.Single(diff.GetProperty("changes").EnumerateArray());
        Assert.Equal("Draft", Text(change.GetProperty("before")));
        Assert.Equal("Approved", Text(change.GetProperty("after")));

        var insertArgs = new { documentHandle, anchor = new { index = 1 }, text = "Inserted", after = false,
            baseRevision = Revision(replaced), operationId = "insert" };
        var inserted = await SuccessAsync(mcp, "insert_word_paragraph", insertArgs);
        AssertWriteStatus(inserted, "Success");
        Assert.Contains("different tool or arguments", await ErrorAsync(mcp, "append_word_paragraph",
            new { documentHandle, insertArgs.text, insertArgs.baseRevision, insertArgs.operationId }));
        var appended = await SuccessAsync(mcp, "append_word_paragraph",
            new { documentHandle, text = "Appendix", baseRevision = Revision(inserted), operationId = "append" });
        AssertWriteStatus(appended, "Success");
        var commentArgs = new { documentHandle, target = new { index = 2 }, text = "Reviewed", author = "Reviewer",
            initials = "R", baseRevision = Revision(appended), operationId = "comment" };
        var commented = await SuccessAsync(mcp, "add_word_comment", commentArgs);
        AssertWriteStatus(commented, "Success");
        var deleted = await SuccessAsync(mcp, "delete_word_paragraph",
            new { documentHandle, target = new { index = 1 }, baseRevision = Revision(commented) });
        AssertWriteStatus(deleted, "Success");
        Assert.True(JsonElement.DeepEquals(inserted, await SuccessAsync(mcp, "insert_word_paragraph", insertArgs)));
        Assert.True(JsonElement.DeepEquals(commented, await SuccessAsync(mcp, "add_word_comment", commentArgs)));
        Assert.Equal(Revision(deleted), Revision(await SuccessAsync(mcp, "sync_word_document", new { documentHandle })));

        using var saved = WordprocessingDocument.Open(new MemoryStream(await http.GetByteArrayAsync(WordPath)), false);
        var mainPart = saved.MainDocumentPart!;
        Assert.Equal(new[] { "Agenda", "Approved", "Appendix" },
            mainPart.Document!.Body!.Elements<W.Paragraph>().Select(paragraph => paragraph.InnerText));
        Assert.Equal("Reviewed", Assert.Single(mainPart.WordprocessingCommentsPart!.Comments!.Elements<W.Comment>()).InnerText);
        Assert.True((await SuccessAsync(mcp, "close_word_document", new { documentHandle })).GetProperty("closed").GetBoolean());
        await AssertReleasedAsync(host, document.ResourceId);
    }

    [Fact]
    public async Task PowerPointToolsReadEditDiffAndCloseOverHttp()
    {
        await using var host = await McpTestHost.StartAsync();
        var document = await host.CreateOfficeDocumentAsync(PresentationPath, PresentationPackage());
        using var http = host.CreateHttp("owner");
        var mcp = new McpHttpSession(http);
        await mcp.InitializeAsync();

        var opened = await SuccessAsync(mcp, "open_presentation", new { resourceId = document.ResourceId });
        var presentationHandle = opened.GetProperty("presentationHandle").GetString();
        var initialRevision = Revision(opened);
        var slides = await SuccessAsync(mcp, "list_slides", new { presentationHandle });
        Assert.Equal(initialRevision, Revision(slides));
        var slideId = Assert.Single(slides.GetProperty("slides").EnumerateArray()).GetProperty("id").GetUInt32();
        var slide = await ReadSlideAsync(mcp, presentationHandle, slideId);
        var shape = Assert.Single(slide.GetProperty("shapes").EnumerateArray());
        var shapeId = shape.GetProperty("id").GetUInt32();
        Assert.Equal("Draft", Text(shape));
        var editors = await SuccessAsync(mcp, "get_presentation_editors", new { presentationHandle });
        Assert.Single(editors.GetProperty("editors").EnumerateArray());
        await AssertLazyAsync(host, document.ResourceId);

        var shapeEdit = await SuccessAsync(mcp, "set_shape_text",
            new { presentationHandle, slideId, shapeId, text = "Approved", baseRevision = initialRevision });
        AssertWriteStatus(shapeEdit, "Success");
        await AssertWriterAsync(host, document.ResourceId);
        var conflict = await SuccessAsync(mcp, "set_shape_text",
            new { presentationHandle, slideId, shapeId, text = "Rejected", baseRevision = initialRevision });
        AssertWriteStatus(conflict, "Conflict");
        Assert.Single(conflict.GetProperty("conflicts").EnumerateArray());
        var notesEdit = await SuccessAsync(mcp, "set_slide_notes",
            new { presentationHandle, slideId, text = "Speaker notes", baseRevision = Revision(shapeEdit) });
        AssertWriteStatus(notesEdit, "Success");
        var added = await SuccessAsync(mcp, "add_slide",
            new { presentationHandle, layoutFromSlideId = slideId, baseRevision = Revision(notesEdit), operationId = "add" });
        AssertWriteStatus(added, "Success");
        var addedSlideId = added.GetProperty("addedSlideId").GetUInt32();
        Assert.NotEqual(slideId, addedSlideId);
        var firstPage = await SuccessAsync(mcp, "list_slides", new { presentationHandle, maxSlides = 1 });
        var nextIndex = firstPage.GetProperty("nextIndex").GetInt32();
        var nextPage = await SuccessAsync(mcp, "list_slides", new { presentationHandle, startIndex = nextIndex });
        Assert.Equal(addedSlideId, Assert.Single(nextPage.GetProperty("slides").EnumerateArray()).GetProperty("id").GetUInt32());

        var diff = await SuccessAsync(mcp, "diff_presentation",
            new { presentationHandle, beforeRevision = initialRevision, afterRevision = Revision(added), maxChanges = 1 });
        Assert.True(diff.GetProperty("truncated").GetBoolean());
        Assert.Equal("Speaker notes", Assert.Single(diff.GetProperty("changes").EnumerateArray())
            .GetProperty("after").GetProperty("notes").GetString());
        var deleted = await SuccessAsync(mcp, "delete_slide",
            new { presentationHandle, slideId = addedSlideId, baseRevision = Revision(added) });
        AssertWriteStatus(deleted, "Success");
        var staleAdd = await SuccessAsync(mcp, "add_slide",
            new { presentationHandle, layoutFromSlideId = addedSlideId, baseRevision = Revision(added), operationId = "stale" });
        AssertWriteStatus(staleAdd, "Conflict");
        await ErrorAsync(mcp, "read_slide", new { presentationHandle, slideId = addedSlideId });
        Assert.Equal(Revision(deleted), Revision(await SuccessAsync(mcp, "sync_presentation", new { presentationHandle })));

        using var saved = PresentationDocument.Open(new MemoryStream(await http.GetByteArrayAsync(PresentationPath)), false);
        var savedSlide = Assert.Single(saved.PresentationPart!.SlideParts);
        Assert.Equal("Approved", Assert.Single(savedSlide.Slide!.Descendants<A.Text>()).Text);
        Assert.Equal("Speaker notes", Assert.Single(savedSlide.NotesSlidePart!.NotesSlide!.Descendants<A.Text>()).Text);
        Assert.True((await SuccessAsync(mcp, "close_presentation", new { presentationHandle })).GetProperty("closed").GetBoolean());
        await AssertReleasedAsync(host, document.ResourceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficeCallsCheckBearerAndCurrentDocumentAccess(bool presentation)
    {
        await using var host = await McpTestHost.StartAsync();
        var document = presentation
            ? await host.CreateOfficeDocumentAsync(PresentationPath, PresentationPackage())
            : await host.CreateOfficeDocumentAsync(WordPath, WordPackage());
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        using var http = host.CreateHttp("peer");
        using var anonymous = host.CreateHttp();
        using var wrongActor = host.CreateHttp("owner");
        var mcp = new McpHttpSession(http);
        await mcp.InitializeAsync();

        var opened = await SuccessAsync(mcp, presentation ? "open_presentation" : "open_word_document",
            new { resourceId = document.ResourceId });
        var handle = opened.GetProperty(presentation ? "presentationHandle" : "documentHandle").GetString();
        var baseRevision = Revision(opened);
        var read = presentation ? "list_slides" : "get_word_summary";
        object readArgs = presentation ? new { presentationHandle = handle } : new { documentHandle = handle };
        var write = presentation ? "add_slide" : "append_word_paragraph";
        const string operationId = "no-write-access";
        object writeArgs = presentation
            ? new { presentationHandle = handle, baseRevision, operationId }
            : new { documentHandle = handle, text = "Denied", baseRevision, operationId };
        using var noBearer = await mcp.SendAsync("tools/call", new { name = read, arguments = readArgs }, requestClient: anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, noBearer.StatusCode);
        using var otherBearer = await mcp.SendAsync("tools/call", new { name = read, arguments = readArgs }, requestClient: wrongActor);
        Assert.Equal(HttpStatusCode.NotFound, otherBearer.StatusCode);

        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read);
        await ErrorAsync(mcp, write, writeArgs);
        Assert.Equal(baseRevision, Revision(await SuccessAsync(mcp, read, readArgs)));
        await AssertLazyAsync(host, document.ResourceId);
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.None);
        await ErrorAsync(mcp, read, readArgs);
        await AssertReleasedAsync(host, document.ResourceId);
    }

    [Fact]
    public async Task OfficeHandlesAreBoundToFormatAndTransportSessionAndShareExcelQuota()
    {
        await using var host = await McpTestHost.StartAsync(configure: options => options.MaxConcurrentSessionsPerActor = 2);
        var workbook = await host.CreateWorkbookAsync();
        var word = await host.CreateOfficeDocumentAsync(WordPath, WordPackage());
        var presentation = await host.CreateOfficeDocumentAsync(PresentationPath, PresentationPackage());
        using var http = host.CreateHttp("owner");
        var mcp = new McpHttpSession(http);
        var otherSession = new McpHttpSession(http);
        await mcp.InitializeAsync();
        await otherSession.InitializeAsync();
        await ErrorAsync(mcp, "open_word_document", new { resourceId = presentation.ResourceId });
        await ErrorAsync(mcp, "open_presentation", new { resourceId = word.ResourceId });
        var opened = await SuccessAsync(mcp, "open_word_document", new { resourceId = word.ResourceId });
        var documentHandle = opened.GetProperty("documentHandle").GetString();
        var baseRevision = Revision(opened);
        var otherOpened = await SuccessAsync(otherSession, "open_word_document", new { resourceId = word.ResourceId });
        Assert.NotEqual(documentHandle, otherOpened.GetProperty("documentHandle").GetString());
        Assert.NotEqual(baseRevision, Revision(otherOpened));
        await ErrorAsync(otherSession, "get_word_summary", new { documentHandle });
        var foreignRevision = await SuccessAsync(mcp, "append_word_paragraph",
            new { documentHandle, text = "Ignored", baseRevision = Revision(otherOpened), operationId = "foreign-revision" });
        AssertWriteStatus(foreignRevision, "BaseRevisionUnavailable");
        await ErrorAsync(mcp, "close_presentation", new { presentationHandle = documentHandle });
        await ErrorAsync(mcp, "close_workbook", new { workbookHandle = documentHandle });
        await ErrorAsync(mcp, "add_slide",
            new { presentationHandle = documentHandle, baseRevision, operationId = "wrong-format" });
        Assert.Equal(baseRevision, Revision(await SuccessAsync(mcp, "get_word_summary", new { documentHandle })));
        Assert.Empty((await host.ReadControlAsync(word.ResourceId)).Coordination.SchemaOwners);
        Assert.Contains("session limit was reached", await ErrorAsync(mcp, "open_workbook", new { resourceId = workbook.ResourceId }));
        Assert.Contains("session limit was reached",
            await ErrorAsync(mcp, "open_presentation", new { resourceId = presentation.ResourceId }));

        await SuccessAsync(mcp, "close_word_document", new { documentHandle });
        var deck = await SuccessAsync(mcp, "open_presentation", new { resourceId = presentation.ResourceId });
        var presentationHandle = deck.GetProperty("presentationHandle").GetString();
        await ErrorAsync(mcp, "close_word_document", new { documentHandle = presentationHandle });
        await ErrorAsync(mcp, "append_word_paragraph",
            new { documentHandle = presentationHandle, text = "Ignored", baseRevision = Revision(deck), operationId = "wrong-format" });
        await AssertLazyAsync(host, presentation.ResourceId);
        var foreignAdd = await SuccessAsync(mcp, "add_slide", new { presentationHandle, baseRevision, operationId = "foreign-revision" });
        AssertWriteStatus(foreignAdd, "BaseRevisionUnavailable");
        await mcp.EndAsync();
        await AssertReleasedAsync(host, presentation.ResourceId);
    }

    [Fact]
    public async Task OfficeToolAnnotationsDistinguishReadsAdditionsAndDestructiveEdits()
    {
        await using var host = await McpTestHost.StartAsync();
        using var http = host.CreateHttp("owner");
        var mcp = new McpHttpSession(http);
        await mcp.InitializeAsync();
        using var listing = await mcp.SendAsync("tools/list");
        var tools = McpHttpSession.AssertResult(await McpHttpSession.ReadMessageAsync(listing))
            .GetProperty("tools").EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
        string[] reads =
        [
            "open_word_document", "get_word_editors", "sync_word_document", "get_word_summary",
            "list_word_headings", "read_word_paragraphs", "diff_word_document",
            "open_presentation", "get_presentation_editors", "sync_presentation", "list_slides",
            "read_slide", "diff_presentation",
        ];
        string[] additive =
        [
            "insert_word_paragraph", "append_word_paragraph", "add_word_comment", "add_slide",
        ];
        string[] destructive =
        [
            "replace_word_paragraph", "delete_word_paragraph", "set_shape_text", "set_slide_notes", "delete_slide",
        ];
        foreach (var name in reads)
            Assert.True(tools[name].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        foreach (var name in additive.Concat(destructive))
        {
            var tool = tools[name];
            var annotations = tool.GetProperty("annotations");
            Assert.False(annotations.TryGetProperty("readOnlyHint", out var readOnly) && readOnly.GetBoolean());
            Assert.Equal(destructive.Contains(name),
                !annotations.TryGetProperty("destructiveHint", out var destructiveHint) || destructiveHint.GetBoolean());
            var required = tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray()
                .Select(item => item.GetString()).ToArray();
            Assert.Contains("baseRevision", required);
            Assert.Equal(additive.Contains(name), required.Contains("operationId"));
            Assert.Equal(additive.Contains(name),
                annotations.TryGetProperty("idempotentHint", out var idempotent) && idempotent.GetBoolean());
        }
        foreach (var name in new[] { "close_word_document", "close_presentation" })
            Assert.False(tools[name].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvedOfficeCommitRequiresTheSameToolAndArguments(bool presentation)
    {
        SaveFailureRepository? repository = null;
        await using var host = await McpTestHost.StartAsync(wrapRepository: inner => repository = new(inner));
        var document = presentation
            ? await host.CreateOfficeDocumentAsync(PresentationPath, PresentationPackage())
            : await host.CreateOfficeDocumentAsync(WordPath, WordPackage());
        using var http = host.CreateHttp("owner");
        var mcp = new McpHttpSession(http);
        await mcp.InitializeAsync();
        var opened = await SuccessAsync(mcp, presentation ? "open_presentation" : "open_word_document",
            new { resourceId = document.ResourceId });
        var handle = opened.GetProperty(presentation ? "presentationHandle" : "documentHandle").GetString();
        var baseRevision = Revision(opened);
        var write = presentation ? "add_slide" : "append_word_paragraph";
        const string operationId = "lost-save-response";
        object arguments = presentation
            ? new { presentationHandle = handle, baseRevision, operationId }
            : new { documentHandle = handle, text = "Appendix", baseRevision, operationId };
        object changedArguments = presentation
            ? new { presentationHandle = handle, baseRevision, layoutFromSlideId = 256, operationId }
            : new { documentHandle = handle, text = "Different appendix", baseRevision, operationId };
        object changedId = presentation
            ? new { presentationHandle = handle, baseRevision, operationId = "another-operation" }
            : new { documentHandle = handle, text = "Appendix", baseRevision, operationId = "another-operation" };
        var contentVersion = (await host.ReadControlAsync(document.ResourceId)).Head.ContentVersion;
        repository!.DropSaveResponses = true;
        var unknown = await SuccessAsync(mcp, write, arguments);
        AssertWriteStatus(unknown, "OutcomeUnknown");
        Assert.Equal(baseRevision, Revision(unknown));
        Assert.DoesNotContain(SaveFailureRepository.Error, unknown.GetProperty("detail").GetString());
        Assert.Equal(contentVersion + 1, (await host.ReadControlAsync(document.ResourceId)).Head.ContentVersion);

        repository.DropSaveResponses = false;
        Assert.Contains("different tool or arguments", await ErrorAsync(mcp, write, changedArguments));
        Assert.Contains("same arguments", await ErrorAsync(mcp, write, changedId));
        var sync = presentation ? "sync_presentation" : "sync_word_document";
        object handleArguments = presentation ? new { presentationHandle = handle } : new { documentHandle = handle };
        Assert.Contains("same arguments", await ErrorAsync(mcp, sync, handleArguments));
        var replay = await SuccessAsync(mcp, write, arguments);
        AssertWriteStatus(replay, "Success");
        Assert.True(JsonElement.DeepEquals(replay, await SuccessAsync(mcp, write, arguments)));
        Assert.Equal(contentVersion + 1, (await host.ReadControlAsync(document.ResourceId)).Head.ContentVersion);
        Assert.Equal(Revision(replay), Revision(await SuccessAsync(mcp, sync, handleArguments)));
        if (presentation)
        {
            var slides = await SuccessAsync(mcp, "list_slides", handleArguments);
            Assert.Equal(2, slides.GetProperty("slides").GetArrayLength());
            Assert.Equal(replay.GetProperty("addedSlideId").GetUInt32(),
                slides.GetProperty("slides")[1].GetProperty("id").GetUInt32());
        }
        else
        {
            var summary = await SuccessAsync(mcp, "get_word_summary", handleArguments);
            Assert.Equal(3, summary.GetProperty("paragraphCount").GetInt32());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedOfficeWriteReplaysAfterLosingMcpHttpResponse(bool presentation)
    {
        await using var host = await McpTestHost.StartAsync();
        var document = presentation
            ? await host.CreateOfficeDocumentAsync(PresentationPath, PresentationPackage())
            : await host.CreateOfficeDocumentAsync(WordPath, WordPackage());
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        using var http = host.CreateHttp("peer");
        var mcp = new McpHttpSession(http);
        await mcp.InitializeAsync();
        var opened = await SuccessAsync(mcp, presentation ? "open_presentation" : "open_word_document",
            new { resourceId = document.ResourceId });
        var handle = opened.GetProperty(presentation ? "presentationHandle" : "documentHandle").GetString();
        var baseRevision = Revision(opened);
        const string operationId = "lost-mcp-response";
        var name = presentation ? "add_slide" : "append_word_paragraph";
        object arguments = presentation
            ? new { presentationHandle = handle, baseRevision, operationId }
            : new { documentHandle = handle, text = "Appendix", baseRevision, operationId };
        var version = (await host.ReadControlAsync(document.ResourceId)).Head.ContentVersion;
        var dropResponse = new DropMcpResponseHandler();
        using var interrupted = new HttpClient(dropResponse) { BaseAddress = host.Origin, Timeout = http.Timeout };
        interrupted.DefaultRequestHeaders.Authorization = http.DefaultRequestHeaders.Authorization;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            mcp.SendAsync("tools/call", new { name, arguments }, requestClient: interrupted));
        var confirmed = dropResponse.DroppedResult.GetProperty("structuredContent");
        AssertWriteStatus(confirmed, "Success");
        Assert.Equal(version + 1, (await host.ReadControlAsync(document.ResourceId)).Head.ContentVersion);

        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read);
        await ErrorAsync(mcp, name, arguments);
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        Assert.True(JsonElement.DeepEquals(confirmed, await SuccessAsync(mcp, name, arguments)));

        object changedRevision = presentation
            ? new { presentationHandle = handle, baseRevision = Revision(confirmed), operationId }
            : new { documentHandle = handle, text = "Appendix", baseRevision = Revision(confirmed), operationId };
        Assert.Contains("different tool or arguments", await ErrorAsync(mcp, name, changedRevision));
        Assert.Equal(version + 1, (await host.ReadControlAsync(document.ResourceId)).Head.ContentVersion);
        if (presentation)
        {
            var slides = await SuccessAsync(mcp, "list_slides", new { presentationHandle = handle });
            Assert.Equal(2, slides.GetProperty("slides").GetArrayLength());
            Assert.Equal(confirmed.GetProperty("addedSlideId").GetUInt32(),
                slides.GetProperty("slides")[1].GetProperty("id").GetUInt32());
        }
        else
        {
            var paragraphs = await SuccessAsync(mcp, "read_word_paragraphs", new { documentHandle = handle });
            Assert.Equal(new[] { "Agenda", "Draft", "Appendix" },
                paragraphs.GetProperty("paragraphs").EnumerateArray().Select(Text));
        }
    }

    [Fact]
    public async Task OfficeResponseBudgetRejectsOversizedContentAndKeepsHandle()
    {
        await using var host = await McpTestHost.StartAsync();
        var document = await host.CreateOfficeDocumentAsync(WordPath, WordPackage(new string('x', 300_000)));
        using var http = host.CreateHttp("owner");
        var mcp = new McpHttpSession(http);
        await mcp.InitializeAsync();
        var opened = await SuccessAsync(mcp, "open_word_document", new { resourceId = document.ResourceId });
        var documentHandle = opened.GetProperty("documentHandle").GetString();
        Assert.Contains("response is too large",
            await ErrorAsync(mcp, "read_word_paragraphs", new { documentHandle, startIndex = 1 }));
        var smallPage = await SuccessAsync(mcp, "read_word_paragraphs", new { documentHandle, maxParagraphs = 1 });
        Assert.Equal("Agenda", Text(Assert.Single(smallPage.GetProperty("paragraphs").EnumerateArray())));
        Assert.Equal(Revision(opened), Revision(smallPage));
    }

    private static async Task<JsonElement> ReadSlideAsync(McpHttpSession mcp, string? presentationHandle, uint slideId) =>
        (await SuccessAsync(mcp, "read_slide", new { presentationHandle, slideId })).GetProperty("slide");

    private static string? Text(JsonElement item) => item.GetProperty("text").GetString();

    private static async Task AssertLazyAsync(McpTestHost host, Guid resourceId)
    {
        var control = await host.ReadControlAsync(resourceId);
        Assert.False(Assert.Single(control.Editors).AsEditor);
        Assert.Empty(control.Coordination.SchemaOwners);
    }

    private static async Task AssertWriterAsync(McpTestHost host, Guid resourceId)
    {
        var control = await host.ReadControlAsync(resourceId);
        Assert.True(Assert.Single(control.Editors).AsEditor);
        Assert.Equal(McpTestHost.Owner.Identity.Subject, Assert.Single(control.Coordination.SchemaOwners).OwnerSubject);
    }

    private static byte[] WordPackage(string text = "Draft")
    {
        using var stream = new MemoryStream();
        stream.Write(OfficeDocumentTemplates.CreateWordDocument());
        stream.Position = 0;
        using (var package = WordprocessingDocument.Open(stream, true))
        {
            var body = package.MainDocumentPart!.Document!.Body!;
            var first = body.GetFirstChild<W.Paragraph>()!;
            first.Append(new W.ParagraphProperties(new W.OutlineLevel { Val = 0 }), new W.Run(new W.Text("Agenda")));
            body.InsertAfter(new W.Paragraph(new W.Run(new W.Text(text))), first);
        }
        return stream.ToArray();
    }

    private static byte[] PresentationPackage()
    {
        using var stream = new MemoryStream();
        stream.Write(OfficeDocumentTemplates.CreatePowerPointPresentation());
        stream.Position = 0;
        using (var package = PresentationDocument.Open(stream, true))
        {
            var tree = package.PresentationPart!.SlideParts.Single().Slide!.CommonSlideData!.ShapeTree!;
            tree.Append(new P.Shape(
                new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 2, Name = "Title" },
                    new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(),
                new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text("Draft"))))));
        }
        return stream.ToArray();
    }

    private sealed class DropMcpResponseHandler() : DelegatingHandler(new HttpClientHandler())
    {
        public JsonElement DroppedResult { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var response = await base.SendAsync(request, cancellationToken);
            DroppedResult = McpHttpSession.AssertResult(await McpHttpSession.ReadMessageAsync(response));
            throw new HttpRequestException("The MCP response was dropped.");
        }
    }

    private sealed class SaveFailureRepository(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public const string Error = "Injected save failure.";

        private volatile bool _committed;
        public volatile bool DropSaveResponses;

        public override async ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication,
            CancellationToken ct = default)
        {
            var result = await base.PublishAsync(publication, ct);
            if (DropSaveResponses && result.Status == PublicationStatus.Accepted)
            {
                _committed = true;
                throw new IOException(Error);
            }
            return result;
        }

        public override ValueTask<SavePreparationRead?> PrepareSaveAsync(Guid id, SavePreparationRequest request,
            CancellationToken ct = default) => DropSaveResponses && _committed
            ? ValueTask.FromException<SavePreparationRead?>(new IOException(Error))
            : base.PrepareSaveAsync(id, request, ct);
    }
}
