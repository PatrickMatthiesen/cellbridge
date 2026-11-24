using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CellBridge.Client.PowerPoint;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace CellBridge.Client.Tests;

public sealed class PowerPointPresentationTests
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string SlideOne = "ppt/slides/slide1.xml";
    private const string SlideOneRelationships = "ppt/slides/_rels/slide1.xml.rels";
    private const string Presentation = "ppt/presentation.xml";
    private const string Layout = "ppt/slideLayouts/slideLayout1.xml";
    private const string Theme = "ppt/theme/theme1.xml";

    [Fact]
    public void OpenAndUnchangedSerializationPreserveTheEntirePackage()
    {
        var bytes = Deck();
        var model = PowerPointPresentation.Open(bytes);
        Assert.Equal(bytes, model.ToArray());

        var slide = Assert.Single(model.ListSlides());
        Assert.Equal(256u, slide.Id);
        Assert.Equal(0, slide.Position);
        Assert.Null(slide.Notes);
        Assert.Equal([new(2, "Title", "Original title"), new(3, "Content", "Original content")], slide.Shapes);
        Assert.Throws<KeyNotFoundException>(() => model.ReadSlide(257));
        Assert.Throws<InvalidDataException>(() => PowerPointPresentation.Open([0, 1, 2]));

        var copy = model.ToArray();
        copy[0] = 0;
        bytes[0] = 0;
        Assert.Equal((byte)'P', model.ToArray()[0]);
    }

    [Fact]
    public void TextEditsRetainFormattingAndUntouchedZipRecords()
    {
        var bytes = Deck();
        var model = PowerPointPresentation.Open(bytes);
        var beforeShape = Shape(Part(bytes, SlideOne), 3).ToString(SaveOptions.DisableFormatting);
        model.SetShapeText(256, 2, "12345678🧪 new title\r\nsecond line");
        var edited = model.ToArray();
        Assert.Equal("12345678🧪 new title\nsecond line", PowerPointPresentation.Open(edited).ReadSlide(256).Shapes[0].Text);
        Assert.Equal(beforeShape, Shape(Part(edited, SlideOne), 3).ToString(SaveOptions.DisableFormatting));
        Assert.Equal(["12345678", "🧪 new title", "second line"],
            Shape(Part(edited, SlideOne), 2).Descendants(A + "t").Select(text => text.Value));
        Assert.Equal("1", (string?)Shape(Part(edited, SlideOne), 2).Descendants(A + "rPr").First().Attribute("b"));
        Assert.Equal("center", (string?)Part(edited, SlideOne).Root!.Element(P + "cSld")!.Attribute("name"));
        foreach (var unchanged in new[] { "[Content_Types].xml", Presentation, SlideOneRelationships, Layout, Theme })
            AssertSameZipRecord(bytes, edited, unchanged);
        Validate(edited);

        model.SetShapeText(256, 2, "12345678🧪 new title\nsecond line");
        Assert.Equal(edited, model.ToArray());
    }

    [Fact]
    public void TextEditsRejectChangesToBulletedParagraphsWithSoftBreaks()
    {
        var bytes = Rewrite(Deck(), SlideOne, slide =>
        {
            var paragraph = Shape(slide, 2).Descendants(A + "p").Single();
            paragraph.AddFirst(new XElement(A + "pPr", new XElement(A + "buChar", new XAttribute("char", "•"))));
            paragraph.Elements(A + "r").First().AddAfterSelf(new XElement(A + "br"));
        });
        Validate(bytes);
        var model = PowerPointPresentation.Open(bytes);

        var original = Assert.IsType<string>(model.ReadSlide(256).Shapes[0].Text);
        Assert.Equal("Original \ntitle", original);
        model.SetShapeText(256, 2, original);
        Assert.Equal(bytes, model.ToArray());

        Assert.Throws<InvalidOperationException>(() => model.SetShapeText(256, 2, "Edited\ntitle"));
        Assert.Equal(bytes, model.ToArray());
    }

    [Fact]
    public void FirstNotesOnBlankDeckUseASeparateTheme()
    {
        var original = OfficeDocumentTemplates.CreatePowerPointPresentation();
        var model = PowerPointPresentation.Open(original);
        model.SetSlideNotes(256, "Speaker notes");
        var result = model.ToArray();
        Validate(result);

        using var document = PresentationDocument.Open(new MemoryStream(result), false);
        var presentation = document.PresentationPart!;
        var slideTheme = Assert.Single(presentation.SlideMasterParts).ThemePart!;
        var notesMaster = presentation.NotesMasterPart!;
        var notesTheme = notesMaster.ThemePart!;
        Assert.Equal("/" + Theme, slideTheme.Uri.OriginalString);
        Assert.Equal("/ppt/theme/theme2.xml", notesTheme.Uri.OriginalString);
        Assert.Equal("application/vnd.openxmlformats-officedocument.theme+xml", notesTheme.ContentType);
        Assert.True(XNode.DeepEquals(Part(result, Theme), Part(result, "ppt/theme/theme2.xml")));
        Assert.Equal(notesMaster.Uri, Assert.Single(presentation.SlideParts).NotesSlidePart!.NotesMasterPart!.Uri);
        Assert.Equal("Speaker notes", PowerPointPresentation.Open(result).ReadSlide(256).Notes);
        foreach (var unchanged in new[] { SlideOne, Layout, "ppt/slideMasters/slideMaster1.xml", Theme })
            AssertSameZipRecord(original, result, unchanged);
    }

    [Fact]
    public void NotesThemeSkipsTheDirectoryOfAnExistingThemeImage()
    {
        const string image = "ppt/theme/theme2.xml/background.png";
        const string themeRelationships = "ppt/theme/_rels/theme1.xml.rels";
        var template = Rewrite(OfficeDocumentTemplates.CreatePowerPointPresentation(), "[Content_Types].xml",
            types => types.Root!.Add(new XElement(types.Root.Name.Namespace + "Override",
                new XAttribute("PartName", "/" + image), new XAttribute("ContentType", "image/png"))));
        using var bytes = new MemoryStream();
        bytes.Write(template);
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Update, leaveOpen: true))
        {
            using (var target = zip.CreateEntry(image).Open())
                target.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a2ioAAAAASUVORK5CYII="));
            using var relationships = zip.CreateEntry(themeRelationships).Open();
            XElement.Parse("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="theme2.xml/background.png"/>
                </Relationships>
                """).Save(relationships);
        }

        var original = bytes.ToArray();
        var model = PowerPointPresentation.Open(original);
        model.SetSlideNotes(256, "Speaker notes");
        var result = model.ToArray();
        Validate(result);
        Assert.True(ContainsPart(result, "ppt/theme/theme3.xml"));
        Assert.True(XNode.DeepEquals(Part(original, themeRelationships), Part(result, "ppt/theme/_rels/theme3.xml.rels")));
        AssertSameZipRecord(original, result, image);
    }

    [Fact]
    public void NotesCreationAndDeletionKeepTheLayoutAndSharedNotesMaster()
    {
        var original = Deck();
        var model = PowerPointPresentation.Open(original);
        model.SetSlideNotes(256, "");
        Assert.Equal(original, model.ToArray());
        model.SetSlideNotes(256, "First paragraph\nSecond paragraph");
        var next = model.AddSlide(256);
        model.SetSlideNotes(next, "Another slide's notes");
        var withNotes = model.ToArray();
        Validate(withNotes);
        model = PowerPointPresentation.Open(withNotes);
        Assert.Equal("First paragraph\nSecond paragraph", model.ReadSlide(256).Notes);
        Assert.Equal("Another slide's notes", model.ReadSlide(next).Notes);
        Assert.Single(Part(withNotes, Presentation).Descendants(P + "notesMasterId"));
        Assert.Equal("Original title", model.ReadSlide(256).Shapes[0].Text);
        AssertSameZipRecord(original, withNotes, SlideOne);

        model.DeleteSlide(256);
        var withoutFirst = model.ToArray();
        Validate(withoutFirst);
        var remaining = Assert.Single(PowerPointPresentation.Open(withoutFirst).ListSlides());
        Assert.Equal(next, remaining.Id);
        Assert.Equal("Another slide's notes", remaining.Notes);
        Assert.False(ContainsPart(withoutFirst, SlideOne));
        Assert.False(ContainsPart(withoutFirst, SlideOneRelationships));
        Assert.False(ContainsPart(withoutFirst, "ppt/notesSlides/notesSlide1.xml"));
        Assert.False(ContainsPart(withoutFirst, "ppt/notesSlides/_rels/notesSlide1.xml.rels"));
        Assert.DoesNotContain(Part(withoutFirst, "[Content_Types].xml").Descendants(Ct + "Override"),
            item => (string?)item.Attribute("PartName") == "/" + SlideOne);
        AssertSameZipRecord(withNotes, withoutFirst, Layout);
        AssertSameZipRecord(withNotes, withoutFirst, "ppt/notesMasters/notesMaster1.xml");

        model.SetSlideNotes(next, "");
        Assert.Equal("", PowerPointPresentation.Open(model.ToArray()).ReadSlide(next).Notes);
    }

    [Fact]
    public void AddedSlideKeepsOnlyItsLayoutAndCanBeAddedToAnEmptyDeck()
    {
        var original = Deck();
        var model = PowerPointPresentation.Open(original);
        model.SetSlideNotes(256, "Do not clone these notes");
        var next = model.AddSlide(256);
        var added = model.ReadSlide(next);
        Assert.Empty(added.Shapes);
        Assert.Null(added.Notes);
        var result = model.ToArray();
        var relationship = Assert.Single(Part(result, "ppt/slides/_rels/slide2.xml.rels").Root!.Elements());
        Assert.Equal(R.NamespaceName + "/slideLayout", (string?)relationship.Attribute("Type"));
        Assert.Equal("../slideLayouts/slideLayout1.xml", (string?)relationship.Attribute("Target"));
        Validate(result);

        model.DeleteSlide(256);
        model.DeleteSlide(next);
        Assert.Empty(model.ListSlides());
        Validate(model.ToArray());
        var newId = model.AddSlide();
        Assert.Equal(newId, Assert.Single(model.ListSlides()).Id);
        Validate(model.ToArray());
    }

    [Fact]
    public void DiffReportsChangesBySlideId()
    {
        var original = PowerPointPresentation.Open(Deck());
        var latest = PowerPointPresentation.Open(original.ToArray());
        latest.SetShapeText(256, 2, "Changed");
        latest.SetSlideNotes(256, "Notes");
        var added = latest.AddSlide();
        var changes = original.Diff(latest);
        Assert.Collection(changes,
            change =>
            {
                Assert.Equal(256u, change.SlideId);
                Assert.Equal("Original title", change.Before!.Shapes[0].Text);
                Assert.Equal("Changed", change.After!.Shapes[0].Text);
                Assert.Equal("Notes", change.After.Notes);
            },
            change =>
            {
                Assert.Equal(added, change.SlideId);
                Assert.Null(change.Before);
                Assert.NotNull(change.After);
            });

        latest.DeleteSlide(256);
        var deletion = Assert.Single(original.Diff(latest), change => change.SlideId == 256);
        Assert.NotNull(deletion.Before);
        Assert.Null(deletion.After);
        Assert.Empty(original.Diff(original));
    }

    [Fact]
    public void ReorderPreservesSlideIdentityForDiffAndMerge()
    {
        var original = PowerPointPresentation.Open(Deck());
        var next = original.AddSlide();
        var latest = PowerPointPresentation.Open(Rewrite(original.ToArray(), Presentation, presentation =>
        {
            var slides = presentation.Root!.Element(P + "sldIdLst")!;
            var entries = slides.Elements().Reverse().ToArray();
            slides.ReplaceNodes(entries);
        }));
        Assert.Equal(new[] { next, 256u }, latest.ListSlides().Select(slide => slide.Id));
        Assert.Equal(new[] { 256u, next }, original.Diff(latest).Select(change => change.SlideId));

        var update = PowerPointPresentation.Merge(original, latest, new PowerPointSetShapeText(256, 2, "Still the first slide"));
        Assert.True(update.Succeeded, update.Conflict);
        Assert.Equal("Still the first slide", update.Presentation!.ReadSlide(256).Shapes[0].Text);
        Assert.Equal(1, update.Presentation.ReadSlide(256).Position);
    }

    [Fact]
    public void GroupedShapesRemainAddressableAndUnknownExtensionsSurviveAnEdit()
    {
        XNamespace custom = "urn:custom:slide-extension";
        var bytes = Rewrite(Deck(), SlideOne, slide =>
        {
            var grouped = new XElement(Shape(slide, 3));
            Shape(slide, 3).Remove();
            var tree = slide.Root!.Element(P + "cSld")!.Element(P + "spTree")!;
            tree.Add(new XElement(P + "grpSp", new XElement(P + "nvGrpSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", 4), new XAttribute("name", "Group")),
                new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
                new XElement(P + "grpSpPr"), grouped));
            slide.Root.Add(new XElement(P + "extLst", new XElement(P + "ext",
                new XAttribute("uri", "urn:custom:slide-extension"), new XElement(custom + "opaque", "Keep me"))));
        });
        var model = PowerPointPresentation.Open(bytes);
        Assert.Equal(new[] { 2u, 4u, 3u }, model.ReadSlide(256).Shapes.Select(shape => shape.Id));
        Assert.Null(model.ReadSlide(256).Shapes.Single(shape => shape.Id == 4).Text);
        model.SetShapeText(256, 3, "Group member");
        Assert.Equal("Group member", model.ReadSlide(256).Shapes.Single(shape => shape.Id == 3).Text);
        Assert.Equal("Keep me", Part(model.ToArray(), SlideOne).Descendants(custom + "opaque").Single().Value);
        Validate(model.ToArray());
    }

    [Fact]
    public void MergeRebasesOntoUnrelatedShapeNotesAndSlideChanges()
    {
        var original = PowerPointPresentation.Open(Deck());
        var latest = PowerPointPresentation.Open(original.ToArray());
        latest.SetShapeText(256, 3, "Remote content");
        latest.SetSlideNotes(256, "Remote notes");
        var remoteSlide = latest.AddSlide();

        var merge = PowerPointPresentation.Merge(original, latest, new PowerPointSetShapeText(256, 2, "Local title"));
        Assert.True(merge.Succeeded, merge.Conflict);
        var merged = merge.Presentation!;
        Assert.Equal("Original title", latest.ReadSlide(256).Shapes[0].Text);
        Assert.Equal("Local title", merged.ReadSlide(256).Shapes[0].Text);
        Assert.Equal("Remote content", merged.ReadSlide(256).Shapes[1].Text);
        Assert.Equal("Remote notes", merged.ReadSlide(256).Notes);
        Assert.Equal(remoteSlide, merged.ReadSlide(remoteSlide).Id);

        var added = PowerPointPresentation.Merge(original, latest, new PowerPointAddSlide(256));
        Assert.True(added.Succeeded, added.Conflict);
        Assert.NotEqual(remoteSlide, added.AddedSlideId);
        Assert.Equal(3, added.Presentation!.ListSlides().Count);

        var notes = PowerPointPresentation.Merge(original, merged, new PowerPointSetSlideNotes(remoteSlide, "Local notes"));
        Assert.False(notes.Succeeded);
        Validate(added.Presentation.ToArray());
    }

    [Fact]
    public void MergeDetectsTextNotesFormattingReplacementAndDeletionConflicts()
    {
        var original = PowerPointPresentation.Open(Deck());
        var latest = PowerPointPresentation.Open(original.ToArray());
        latest.SetShapeText(256, 2, "Remote title");
        latest.SetSlideNotes(256, "Remote notes");

        Assert.False(PowerPointPresentation.Merge(original, latest, new PowerPointSetShapeText(256, 2, "Local title")).Succeeded);
        Assert.False(PowerPointPresentation.Merge(original, latest, new PowerPointSetSlideNotes(256, "Local notes")).Succeeded);
        Assert.False(PowerPointPresentation.Merge(original, latest, new PowerPointDeleteSlide(256)).Succeeded);
        var exactText = PowerPointPresentation.Merge(original, latest, new PowerPointSetShapeText(256, 2, "Remote title"));
        Assert.True(exactText.Succeeded, exactText.Conflict);
        Assert.Equal(latest.ToArray(), exactText.Presentation!.ToArray());
        var exactNotes = PowerPointPresentation.Merge(original, latest, new PowerPointSetSlideNotes(256, "Remote notes"));
        Assert.True(exactNotes.Succeeded, exactNotes.Conflict);
        Assert.Equal(latest.ToArray(), exactNotes.Presentation!.ToArray());

        var formatting = PowerPointPresentation.Open(Rewrite(original.ToArray(), SlideOne, slide =>
            Shape(slide, 2).Descendants(A + "rPr").First().SetAttributeValue("b", "0")));
        Assert.False(PowerPointPresentation.Merge(original, formatting, new PowerPointSetShapeText(256, 2, "Local title")).Succeeded);

        latest.DeleteSlide(256);
        Assert.Equal(256u, latest.AddSlide());
        Assert.False(PowerPointPresentation.Merge(original, latest, new PowerPointSetSlideNotes(256, "Local notes")).Succeeded);
        Assert.False(PowerPointPresentation.Merge(original, latest, new PowerPointAddSlide(256)).Succeeded);
    }

    [Fact]
    public void MergeDeletesAnUnchangedSlideAndRejectsAChangedLayout()
    {
        var original = PowerPointPresentation.Open(Deck());
        var next = original.AddSlide();
        var peer = PowerPointPresentation.Open(original.ToArray());
        peer.SetSlideNotes(next, "Preserve these notes");

        var deletion = PowerPointPresentation.Merge(original, peer, new PowerPointDeleteSlide(256));
        Assert.True(deletion.Succeeded, deletion.Conflict);
        var remaining = Assert.Single(deletion.Presentation!.ListSlides());
        Assert.Equal(next, remaining.Id);
        Assert.Equal("Preserve these notes", remaining.Notes);

        var layout = PowerPointPresentation.Open(Rewrite(peer.ToArray(), Layout, document =>
            document.Root!.Element(P + "cSld")!.SetAttributeValue("name", "New layout")));
        Assert.False(PowerPointPresentation.Merge(original, layout, new PowerPointAddSlide(256)).Succeeded);
    }

    [Fact]
    public void DeleteRejectsAnIncomingSlideRelationshipAndAnExplicitPresentationReference()
    {
        var model = PowerPointPresentation.Open(Deck());
        var next = model.AddSlide();
        var linked = Rewrite(model.ToArray(), "ppt/slides/_rels/slide2.xml.rels", relationships =>
            relationships.Root!.Add(new XElement(Rel + "Relationship", new XAttribute("Id", "rId2"),
                new XAttribute("Type", R.NamespaceName + "/slide"), new XAttribute("Target", "slide1.xml"))));
        var latest = PowerPointPresentation.Open(linked);
        Assert.Throws<InvalidOperationException>(() => latest.DeleteSlide(256));
        Assert.Equal(linked, latest.ToArray());
        Assert.False(PowerPointPresentation.Merge(model, latest, new PowerPointDeleteSlide(next)).Succeeded);

        var referenced = Rewrite(Deck(), Presentation, presentation =>
            presentation.Root!.Add(new XElement(P + "custShowLst",
                new XElement(P + "custShow", new XAttribute("id", 1), new XAttribute("name", "Show"),
                    new XElement(P + "sldLst", new XElement(P + "sld", new XAttribute(R + "id", "rId2")))))));
        var withShow = PowerPointPresentation.Open(referenced);
        Assert.Throws<InvalidOperationException>(() => withShow.DeleteSlide(256));
        Assert.Equal(referenced, withShow.ToArray());
    }

    [Fact]
    public void AmbiguousShapesAndFieldsFailWithoutMutatingThePackage()
    {
        var duplicate = Rewrite(Deck(), SlideOne, slide =>
            Shape(slide, 3).Descendants(P + "cNvPr").Single().SetAttributeValue("id", 2));
        var model = PowerPointPresentation.Open(duplicate);
        Assert.Throws<InvalidOperationException>(() => model.SetShapeText(256, 2, "Cannot choose"));
        Assert.Equal(duplicate, model.ToArray());

        var fields = Rewrite(Deck(), SlideOne, slide =>
            Shape(slide, 2).Descendants(A + "r").First().ReplaceWith(new XElement(A + "fld",
                new XAttribute("id", "{00000000-0000-0000-0000-000000000001}"), new XElement(A + "t", "Field"))));
        model = PowerPointPresentation.Open(fields);
        Assert.Equal("Fieldtitle", model.ReadSlide(256).Shapes[0].Text);
        Assert.Throws<InvalidOperationException>(() => model.SetShapeText(256, 2, "Cannot overwrite fields"));
        Assert.Equal(fields, model.ToArray());
    }

    private static byte[] Deck() => Rewrite(OfficeDocumentTemplates.CreatePowerPointPresentation(), SlideOne, slide =>
    {
        var data = slide.Root!.Element(P + "cSld")!;
        data.SetAttributeValue("name", "center");
        var tree = data.Element(P + "spTree")!;
        tree.Add(TextBox(2, "Title", "Original ", "title"), TextBox(3, "Content", "Original content"));
    });

    private static XElement TextBox(uint id, string name, params string[] runs) => new(P + "sp",
        new XElement(P + "nvSpPr",
            new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
            new XElement(P + "cNvSpPr", new XAttribute("txBox", 1)), new XElement(P + "nvPr")),
        new XElement(P + "spPr"),
        new XElement(P + "txBody", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"),
            new XElement(A + "p", runs.Select((text, i) => new XElement(A + "r",
                new XElement(A + "rPr", new XAttribute("b", i == 0 ? "1" : "0")),
                new XElement(A + "t", text))))));

    private static XElement Shape(XDocument slide, uint id) => slide.Descendants(P + "sp").Single(
        shape => (uint?)shape.Element(P + "nvSpPr")?.Element(P + "cNvPr")?.Attribute("id") == id);

    private static XDocument Part(byte[] package, string path)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        using var stream = zip.GetEntry(path)!.Open();
        return XDocument.Load(stream);
    }

    private static bool ContainsPart(byte[] package, string path)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        return zip.GetEntry(path) is not null;
    }

    private static byte[] Rewrite(byte[] package, string path, Action<XDocument> edit)
    {
        var part = Part(package, path);
        edit(part);
        using var bytes = new MemoryStream();
        bytes.Write(package);
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry(path)!.Delete();
            using var stream = zip.CreateEntry(path).Open();
            part.Save(stream);
        }
        return bytes.ToArray();
    }

    private static void Validate(byte[] bytes)
    {
        using var document = PresentationDocument.Open(new MemoryStream(bytes), false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2021).Validate(document)
            .Select(error => $"{error.Part?.Uri} {error.Path?.XPath}: {error.Description}").ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
    }

    private static void AssertSameZipRecord(byte[] original, byte[] updated, string name)
    {
        var (originalLocal, originalCentral) = ZipRecord(original, name);
        var (updatedLocal, updatedCentral) = ZipRecord(updated, name);
        Assert.Equal(originalLocal, updatedLocal);
        Assert.Equal(originalCentral, updatedCentral);
    }

    private static (byte[] Local, byte[] Central) ZipRecord(byte[] package, string name)
    {
        var end = package.Length - 22;
        var start = ReadInt32(package, end + 16);
        var entries = new List<(string Name, int Local, int Central, int Length)>();
        for (var offset = start; offset < end;)
        {
            var nameLength = ReadUInt16(package, offset + 28);
            var length = 46 + nameLength + ReadUInt16(package, offset + 30) + ReadUInt16(package, offset + 32);
            entries.Add((Encoding.UTF8.GetString(package, offset + 46, nameLength),
                ReadInt32(package, offset + 42), offset, length));
            offset += length;
        }

        var entry = entries.Single(item => item.Name == name);
        var next = entries.Select(item => item.Local).Where(local => local > entry.Local).DefaultIfEmpty(start).Min();
        var central = package.AsSpan(entry.Central, entry.Length).ToArray();
        central.AsSpan(42, 4).Clear();
        return (package[entry.Local..next], central);
    }

    private static int ReadInt32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    private static ushort ReadUInt16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
}
