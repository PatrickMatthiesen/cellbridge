using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CellBridge.Client.Word;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Wml = DocumentFormat.OpenXml.Wordprocessing;

namespace CellBridge.Client.Tests;

public sealed class WordDocumentTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    private const string Main = "word/document.xml";
    private const string Relationships = "word/_rels/document.xml.rels";

    [Fact]
    public void ReadsDirectBodyParagraphsWithStyleInheritanceAndVisibleRevisionText()
    {
        var original = Create("""
            <w:p w14:paraId="00ABCDEF"><w:pPr><w:pStyle w:val="Inherited"/></w:pPr>
              <w:r><w:t>Heading</w:t></w:r>
            </w:p>
            <w:p w14:paraId="00abcdef"><w:pPr><w:pStyle w:val="Inherited"/><w:outlineLvl w:val="9"/></w:pPr>
              <w:del w:id="1" w:author="A"><w:r><w:delText>old</w:delText></w:r></w:del>
              <w:ins w:id="2" w:author="A"><w:r><w:t>new</w:t><w:tab/><w:t>line</w:t><w:br/><w:t>end</w:t></w:r></w:ins>
            </w:p>
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>table</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """, styles: """
            <w:style w:type="paragraph" w:styleId="Base"><w:pPr><w:outlineLvl w:val="2"/></w:pPr></w:style>
            <w:style w:type="paragraph" w:styleId="Inherited"><w:basedOn w:val="Base"/></w:style>
            """);
        var document = WordDocument.Open(original);
        Assert.Equal(new WordDocumentSummary(2, 1), document.GetSummary());
        Assert.Equal(new WordParagraph(new(0), "Heading", "Inherited", 3), Assert.Single(document.ListHeadings()));
        Assert.Equal(new WordParagraph(new(1), "new\tline\nend", "Inherited", null), Assert.Single(document.ReadParagraphs(1, 1)));
        Assert.Empty(document.ReadParagraphs(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReadParagraphs(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReadParagraphs(0, 0));

        var roundTrip = document.ToArray();
        Assert.Equal(original, roundTrip);
        roundTrip[0] = original[0] = 0;
        Assert.Equal((byte)'P', document.ToArray()[0]);
    }

    [Fact]
    public void TypedEditsPreserveRunFormattingAndUntouchedLocalRecords()
    {
        var original = Create("""
            <w:p w14:paraId="00000001"><w:pPr><w:jc w:val="center"/></w:pPr>
              <w:r><w:rPr><w:b/></w:rPr><w:t>First</w:t></w:r>
              <w:r><w:rPr><w:i/></w:rPr><w:t xml:space="preserve"> part</w:t></w:r>
            </w:p>
            <w:p w14:paraId="00000002"><w:r><w:t>Delete</w:t></w:r></w:p>
            <w:p w14:paraId="00000003"><w:ins w:id="5" w:author="Remote"><w:r><w:t>Already tracked</w:t></w:r></w:ins></w:p>
            """);
        var document = WordDocument.Open(original);
        var paragraphs = document.ReadParagraphs();
        document.ApplyEdits([
            new WordReplaceParagraph(paragraphs[0].Reference, "First 🐈\tpart\r\n"),
            new WordInsertParagraph(paragraphs[0].Reference, "before", After: false),
            new WordInsertParagraph(paragraphs[0].Reference, "after 1"),
            new WordInsertParagraph(paragraphs[0].Reference, "after 2"),
            new WordDeleteParagraph(paragraphs[1].Reference),
            new WordAppendParagraph("append"),
        ]);
        var result = document.ToArray();
        var reopened = WordDocument.Open(result);
        Assert.Equal(["before", "First 🐈\tpart\n", "after 1", "after 2", "Already tracked", "append"],
            reopened.ReadParagraphs().Select(p => p.Text));
        Assert.All(reopened.ReadParagraphs(), p => Assert.NotNull(p.Reference.Id));
        Assert.Equal(6, reopened.ReadParagraphs().Select(p => p.Reference.Id).Distinct().Count());

        var body = Part(result, Main).Root!.Element(W + "body")!;
        var originalTracked = Part(original, Main).Descendants(W + "ins").Single();
        Assert.True(XNode.DeepEquals(originalTracked, body.Descendants(W + "ins").Single()));
        Assert.Equal(W + "sectPr", body.Elements().Last().Name);
        var replaced = body.Elements(W + "p").Single(p => (string?)p.Attribute(W14 + "paraId") == "00000001");
        Assert.Single(replaced.Descendants(W + "b"));
        Assert.Single(replaced.Descendants(W + "i"));
        Assert.Single(replaced.Descendants(W + "jc"));
        foreach (var part in new[] { "_rels/.rels", "[Content_Types].xml", Relationships, "word/settings.xml" })
            Assert.Equal(LocalRecord(original, part), LocalRecord(result, part));
        Validate(result);
    }

    [Fact]
    public void CommentsUseTheMainPartRelationshipAndPreserveExistingCommentIds()
    {
        var document = WordDocument.Open(Create("<w:p><w:r><w:t>Words</w:t></w:r></w:p>"));
        var reference = Assert.Single(document.ReadParagraphs()).Reference;
        document.ApplyEdits([new WordAddComment(reference, "first comment", "Author", "A")]);
        var first = document.ToArray();
        var firstRelationships = LocalRecord(first, Relationships);
        document = WordDocument.Open(first);
        document.ApplyEdits([new WordAddComment(reference, "second\ncomment", "Second Author")]);
        var updated = document.ToArray();
        Assert.Equal(firstRelationships, LocalRecord(updated, Relationships));
        Assert.Equal(LocalRecord(first, "[Content_Types].xml"), LocalRecord(updated, "[Content_Types].xml"));

        using var stream = new MemoryStream(updated);
        using var package = WordprocessingDocument.Open(stream, false);
        var main = package.MainDocumentPart!;
        var comments = main.WordprocessingCommentsPart!.Comments!.Elements<Wml.Comment>().ToArray();
        Assert.Equal(["0", "1"], comments.Select(comment => comment.Id!.Value));
        Assert.Equal(["Author", "Second Author"], comments.Select(comment => comment.Author!.Value));
        Assert.Equal(["0", "1"], main.Document!.Descendants<Wml.CommentReference>().Select(mark => mark.Id!.Value));
        Assert.Single(comments[1].Descendants<Wml.Break>());
        Assert.Equal("Words", Assert.Single(document.ReadParagraphs()).Text);
        Validate(updated);
    }

    [Fact]
    public void RejectsWrongPackageTypeAndInvalidZip()
    {
        Assert.Throws<InvalidDataException>(() => WordDocument.Open(OfficeDocumentTemplates.CreateExcelWorkbook()));
        Assert.Throws<InvalidDataException>(() => WordDocument.Open("invalid zip"u8.ToArray()));
    }

    [Fact]
    public void InvalidOrUnsupportedEditsLeaveThePackageUnchanged()
    {
        var original = Create("""
            <w:p><w:r><w:t>Plain</w:t></w:r></w:p>
            <w:p><w:ins w:id="5" w:author="A"><w:r><w:t>Tracked</w:t></w:r></w:ins></w:p>
            """);
        var document = WordDocument.Open(original);
        Assert.Throws<InvalidOperationException>(() => document.ApplyEdits([
            new WordReplaceParagraph(new(0), "Would otherwise apply"),
            new WordDeleteParagraph(new(1)),
        ]));
        Assert.Throws<InvalidOperationException>(() => document.ApplyEdits([new WordReplaceParagraph(new(1), "Reject")]));
        Assert.Throws<InvalidOperationException>(() => document.ApplyEdits([
            new WordReplaceParagraph(new(0), "first"),
            new WordReplaceParagraph(new(0), "second"),
        ]));
        Assert.Throws<InvalidOperationException>(() => document.ApplyEdits([new WordDeleteParagraph(new(0, "12345678"))]));
        Assert.Throws<ArgumentException>(() => document.ApplyEdits([new WordAddComment(new(0), "comment", " ")]));
        Assert.Throws<System.Xml.XmlException>(() => document.ApplyEdits([new WordAppendParagraph("invalid\0XML")]));
        Assert.Equal(original, document.ToArray());
        document.ApplyEdits([new WordReplaceParagraph(new(0), "Plain")]);
        Assert.Equal(original, document.ToArray());
    }

    [Fact]
    public void DiffReportsUpdatesRemovalsAndAdditionsInOrderAndCanTruncate()
    {
        var original = WordDocument.Open(Create("""
            <w:p w14:paraId="00000001"><w:r><w:t>A</w:t></w:r></w:p>
            <w:p w14:paraId="00000002"><w:r><w:t>B</w:t></w:r></w:p>
            <w:p w14:paraId="00000003"><w:r><w:t>C</w:t></w:r></w:p>
            """));
        var latest = WordDocument.Open(original.ToArray());
        latest.ApplyEdits([
            new WordDeleteParagraph(new(0)),
            new WordReplaceParagraph(new(1), "updated"),
            new WordAppendParagraph("added"),
        ]);
        var full = latest.Diff(original);
        Assert.False(full.Truncated);
        Assert.Collection(full.Changes,
            deletion => { Assert.Equal("A", deletion.Before!.Text); Assert.Null(deletion.After); },
            replacement => { Assert.Equal("B", replacement.Before!.Text); Assert.Equal("updated", replacement.After!.Text); },
            addition => { Assert.Null(addition.Before); Assert.Equal("added", addition.After!.Text); });
        var capped = latest.Diff(original, 2);
        Assert.True(capped.Truncated);
        Assert.Equal(full.Changes.Take(2), capped.Changes);
        Assert.False(latest.Diff(original, 3).Truncated);
        Assert.Empty(original.Diff(original).Changes);
    }

    [Fact]
    public async Task RebasesAnIndexReferenceWithAStableIdAfterAnIndexShift()
    {
        var original = Create("""
            <w:p><w:r><w:t>A</w:t></w:r></w:p>
            <w:p w14:paraId="00000001"><w:r><w:t>B</w:t></w:r></w:p>
            """);
        await using var host = await DocumentSessionScenarioTests.ScenarioHost.StartAsync(false, documentPath: "/shared/word-model.docx");
        await host.CreateAsync(original);
        var options = new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter };
        await using var first = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options);
        await using var second = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options);
        var writer = new WordDocumentSession(first);
        var peer = new WordDocumentSession(second);
        var revision = peer.Revision;
        Assert.Equal(OfficeWriteStatus.Success, (await writer.ApplyAsync(new WordInsertParagraph(new(0), "inserted", false), revision)).Status);
        Assert.Equal(OfficeWriteStatus.Success, (await peer.ApplyAsync(new WordReplaceParagraph(new(1), "updated"), revision)).Status);
        Assert.Equal(["inserted", "A", "updated"], peer.ReadParagraphs().Select(p => p.Text));
    }

    [Fact]
    public async Task RejectsAStaleReplacementWhenAnIdlessParagraphTakesTheTargetsPlace()
    {
        var original = Create("""
            <w:p><w:r><w:t xml:space="preserve">A</w:t></w:r></w:p>
            <w:p><w:r><w:t>B</w:t></w:r></w:p>
            """);
        await using var host = await DocumentSessionScenarioTests.ScenarioHost.StartAsync(false, documentPath: "/shared/word-model.docx");
        var head = await host.CreateAsync(original);
        var options = new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter };
        await using var first = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options);
        await using var second = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options);
        var peer = new WordDocumentSession(second);
        var revision = peer.Revision;

        var writer = WordDocument.Open(original);
        writer.ApplyEdits([new WordDeleteParagraph(new(0)), new WordReplaceParagraph(new(1), "A")]);
        Assert.True((await first.TryCommitAsync(first.GetSnapshot().Revision, writer.ToArray())).Succeeded);

        var stale = await peer.ApplyAsync(new WordReplaceParagraph(new(0), "stale edit"), revision);

        Assert.Equal(OfficeWriteStatus.Conflict, stale.Status);
        Assert.Equal("A", Assert.Single(peer.ReadParagraphs()).Text);
        Assert.Equal(first.GetSnapshot().Package, second.GetSnapshot().Package);
        Assert.Equal(head.ContentVersion + 1, (await host.ReadControlAsync(head.ResourceId)).Head.ContentVersion);
        Assert.Equal(OfficeWriteStatus.Success, (await peer.ApplyAsync(new WordAppendParagraph("appended"), revision)).Status);
        Assert.Equal(["A", "appended"], peer.ReadParagraphs().Select(p => p.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsStaleIndexEditsWithDuplicateIdsInEitherRevision(bool duplicateInBase)
    {
        const string target = """<w:p w14:paraId="0000000A"><w:r><w:t>A</w:t></w:r></w:p>""";
        const string duplicate = """<w:p w14:paraId="0000000a"><w:r><w:t>B</w:t></w:r></w:p>""";
        var original = Create(duplicateInBase ? target + duplicate : target);
        var latest = Create(duplicateInBase ? target : target + duplicate);
        await using var host = await DocumentSessionScenarioTests.ScenarioHost.StartAsync(false, documentPath: "/shared/word-model.docx");
        await host.CreateAsync(original);
        var options = new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter };
        await using var first = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options);
        await using var second = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options);
        var peer = new WordDocumentSession(second);
        var revision = peer.Revision;
        Assert.True((await first.TryCommitAsync(first.GetSnapshot().Revision, latest)).Succeeded);

        var stale = await peer.ApplyAsync(new WordReplaceParagraph(new(0), "stale edit"), revision);

        Assert.Equal(OfficeWriteStatus.Conflict, stale.Status);
        Assert.Equal(latest, second.GetSnapshot().Package);
    }

    [Fact]
    public async Task RejectsAmbiguousIndexAndOccupiedInsertionGapWithoutRewritingLatest()
    {
        var original = Create("""
            <w:p w14:paraId="00000001"><w:r><w:t>Anchor</w:t></w:r></w:p>
            <w:p w14:paraId="00000002"><w:r><w:t>Neighbor</w:t></w:r></w:p>
            <w:p><w:r><w:t>Duplicate</w:t></w:r></w:p>
            <w:p><w:r><w:t>Duplicate</w:t></w:r></w:p>
            """);
        await using var host = await DocumentSessionScenarioTests.ScenarioHost.StartAsync(false, documentPath: "/shared/word-model.docx");
        await host.CreateAsync(original);
        var options = new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter };
        await using var first = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options);
        await using var second = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options);
        var writer = new WordDocumentSession(first);
        var peer = new WordDocumentSession(second);
        var revision = peer.Revision;
        var anchor = peer.ReadParagraphs()[0].Reference;
        Assert.Equal(OfficeWriteStatus.Success, (await writer.ApplyAsync(new WordInsertParagraph(anchor, "inserted"), revision)).Status);
        var latest = first.GetSnapshot().Package;
        Assert.Equal(OfficeWriteStatus.Conflict, (await peer.ApplyAsync(new WordReplaceParagraph(new(3), "wrong"), revision)).Status);
        Assert.Equal(OfficeWriteStatus.Conflict, (await peer.ApplyAsync(new WordInsertParagraph(anchor, "same gap"), revision)).Status);
        Assert.Equal(latest, second.GetSnapshot().Package);
    }

    internal static byte[] CreateBlankParagraphWithId() => Create("""<w:p w14:paraId="00000001"/>""");

    private static byte[] Create(string body, string? styles = null)
    {
        using var stream = new MemoryStream();
        stream.Write(OfficeDocumentTemplates.CreateWordDocument());
        stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            Write(zip, Main, $"""
                <w:document xmlns:w="{W}" xmlns:w14="{W14}" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" mc:Ignorable="w14">
                  <w:body>{body}<w:sectPr/></w:body>
                </w:document>
                """);
            if (styles is not null)
            {
                Write(zip, "word/styles.xml", $"""<w:styles xmlns:w="{W}">{styles}</w:styles>""");
                const string relationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
                var relationships = Read(zip, Relationships);
                relationships.Root!.Add(new XElement(XName.Get("Relationship", relationshipNamespace),
                    new XAttribute("Id", "rIdStyles"), new XAttribute("Target", "styles.xml"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles")));
                Write(zip, Relationships, relationships.ToString());
                var types = Read(zip, "[Content_Types].xml");
                types.Root!.Add(new XElement(types.Root.Name.Namespace + "Override", new XAttribute("PartName", "/word/styles.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml")));
                Write(zip, "[Content_Types].xml", types.ToString());
            }
        }
        return stream.ToArray();
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        zip.GetEntry(name)?.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(text);
    }

    private static XDocument Part(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        return Read(zip, name);
    }

    private static XDocument Read(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static byte[] LocalRecord(byte[] package, string name)
    {
        var directory = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(package.Length - 6));
        var offsets = new List<(string Name, int Offset)>();
        for (var position = directory; position < package.Length - 22;)
        {
            var central = package.AsSpan(position);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(central[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(central[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(central[32..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(central[42..]);
            offsets.Add((Encoding.UTF8.GetString(central.Slice(46, nameLength)), offset));
            position += 46 + nameLength + extraLength + commentLength;
        }
        var start = offsets.Single(entry => entry.Name == name).Offset;
        var end = offsets.Where(entry => entry.Offset > start).Select(entry => entry.Offset).DefaultIfEmpty(directory).Min();
        return package[start..end];
    }

    private static void Validate(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var package = WordprocessingDocument.Open(stream, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(package);
        Assert.Empty(errors.Select(error => error.Description));
    }
}
