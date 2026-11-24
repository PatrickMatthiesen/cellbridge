using System.Xml.Linq;

namespace CellBridge.Client.PowerPoint;

internal sealed partial class PowerPointPackage
{
    internal TextTarget NotesTarget(uint slideId)
    {
        var slide = GetSlide(slideId);
        var slideIdentity = SlideIdentity(slideId);
        var notes = FindRelatedPart(slide.Part, "notesSlide");
        if (notes is null)
            return new(slideIdentity, null, null, null);
        CheckNotesOwner(slide.Part, notes);
        var shape = NotesBody(notes, "notes");
        var text = shape?.Element(P + "txBody");
        return new(slideIdentity, notes + ":" + ShapePropertiesOrNull(shape), ReadText(text),
            text?.ToString(SaveOptions.DisableFormatting));
    }

    internal void SetSlideNotes(uint slideId, string text)
    {
        var slide = GetSlide(slideId);
        var notes = FindRelatedPart(slide.Part, "notesSlide");
        if (notes is null)
        {
            if (text.Length != 0)
                CreateNotes(slide, text);
            return;
        }

        CheckNotesOwner(slide.Part, notes);
        var shape = NotesBody(notes, "notes");
        if (shape is null)
        {
            if (text.Length == 0)
                return;

            var tree = ShapeTree(notes, "notes");
            var master = FindRelatedPart(notes, "notesMaster")
                ?? throw new InvalidOperationException("The notes slide has no notes master.");
            var placeholder = NotesBody(master, "notesMaster")?.Element(P + "nvSpPr")?
                .Element(P + "nvPr")?.Element(P + "ph");
            var ids = tree.Descendants(P + "cNvPr").Select(properties => ReadId(properties, "id")).ToHashSet();
            var id = 2u;
            while (ids.Contains(id))
                id++;
            shape = NotesPlaceholder(id, "Notes Placeholder", "body", placeholder);
            if (tree.Element(P + "extLst") is { } extensions)
                extensions.AddBeforeSelf(shape);
            else
                tree.Add(shape);
        }

        var body = shape.Element(P + "txBody");
        if (body is null)
        {
            body = EmptyTextBody();
            if (shape.Element(P + "extLst") is { } extensions)
                extensions.AddBeforeSelf(body);
            else
                shape.Add(body);
        }
        if (SetText(body, text))
            Change(notes);
    }

    private void CreateNotes(Slide slide, string text)
    {
        var master = FindRelatedPart(PresentationPart, "notesMaster");
        if (master is null)
            master = CreateNotesMaster(slide);
        else if (_presentation.Element(P + "notesMasterIdLst")?.Element(P + "notesMasterId") is not { } reference ||
                 !string.Equals(FindReferencedPart(PresentationPart, reference, "notesMaster"), master,
                     StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The presentation has an unregistered notes master.");

        var masterBody = NotesBody(master, "notesMaster");
        var tree = EmptyShapeTree();
        tree.Add(NotesPlaceholder(2, "Slide Image Placeholder", "sldImg", null));
        var textShape = NotesPlaceholder(3, "Notes Placeholder", "body",
            masterBody?.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph"));
        SetText(textShape.Element(P + "txBody")!, text);
        tree.Add(textShape);
        var notes = NextPart("notesSlides/notesSlide");
        AddPart(notes, PresentationRoot("notes", new XElement(P + "cSld", tree), ColorMapOverride()));
        AddRelationship(notes, master, "notesMaster");
        AddRelationship(notes, slide.Part, "slide");
        AddRelationship(slide.Part, notes, "notesSlide");
        AddContentType(notes, "notesSlide");
    }

    private string CreateNotesMaster(Slide slide)
    {
        if (_presentation.Element(P + "notesMasterIdLst")?.Elements().Any() == true)
            throw new InvalidOperationException("The presentation has an unresolved notes master.");
        var layout = FindRelatedPart(slide.Part, "slideLayout");
        var master = layout is null ? null : FindRelatedPart(layout, "slideMaster");
        var theme = master is null ? null : FindRelatedPart(master, "theme");
        if (theme is null)
            throw new InvalidOperationException("The slide has no theme for a new notes master.");

        var notesMaster = NextPart("notesMasters/notesMaster");
        var tree = EmptyShapeTree();
        var size = _presentation.Element(P + "notesSz");
        var width = (long?)size?.Attribute("cx") ?? 6_858_000L;
        var height = (long?)size?.Attribute("cy") ?? 9_144_000L;
        var image = NotesPlaceholder(2, "Slide Image Placeholder", "sldImg", null);
        SetPlaceholderBounds(image, width / 10, height / 10, width * 4 / 5, height * 2 / 5);
        var body = NotesPlaceholder(3, "Notes Placeholder", "body", null);
        SetPlaceholderBounds(body, width / 10, height * 11 / 20, width * 4 / 5, height * 7 / 20);
        tree.Add(image, body);
        var colorMap = new XElement(P + "clrMap", new XAttribute("bg1", "lt1"), new XAttribute("tx1", "dk1"),
            new XAttribute("bg2", "lt2"), new XAttribute("tx2", "dk2"),
            Enumerable.Range(1, 6).Select(index => new XAttribute($"accent{index}", $"accent{index}")),
            new XAttribute("hlink", "hlink"), new XAttribute("folHlink", "folHlink"));
        AddPart(notesMaster, PresentationRoot("notesMaster", new XElement(P + "cSld", tree), colorMap));
        AddRelationship(notesMaster, theme, "theme");
        AddContentType(notesMaster, "notesMaster");
        var id = AddRelationship(PresentationPart, notesMaster, "notesMaster");
        PresentationList("notesMasterIdLst", "sldMasterIdLst")
            .Add(new XElement(P + "notesMasterId", new XAttribute(R + "id", id)));
        Change(PresentationPart);
        return notesMaster;
    }

    private static void SetPlaceholderBounds(XElement shape, long x, long y, long width, long height)
    {
        shape.Element(P + "spPr")!.Add(new XElement(A + "xfrm",
            new XElement(A + "off", new XAttribute("x", x), new XAttribute("y", y)),
            new XElement(A + "ext", new XAttribute("cx", width), new XAttribute("cy", height))));
    }

    private static XElement NotesPlaceholder(uint id, string name, string type, XElement? inheritance)
    {
        var placeholder = inheritance is null
            ? new XElement(P + "ph", new XAttribute("type", type))
            : new XElement(inheritance);
        if (inheritance is null && type == "body")
            placeholder.Add(new XAttribute("idx", 1));
        return new XElement(P + "sp",
            new XElement(P + "nvSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
                new XElement(P + "cNvSpPr"), new XElement(P + "nvPr", placeholder)),
            new XElement(P + "spPr"), type == "body" ? EmptyTextBody() : null);
    }

    private static XElement EmptyTextBody() => new(P + "txBody",
        new XElement(A + "bodyPr"), new XElement(A + "lstStyle"), new XElement(A + "p"));

    private XElement? NotesBody(string notes, string root)
    {
        var tree = ShapeTree(notes, root);
        var bodies = tree.Descendants(P + "sp").Where(shape =>
            (string?)shape.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph")?.Attribute("type") == "body")
            .ToArray();
        if (bodies.Length > 1 || bodies.Length == 1 && (bodies[0].Parent != tree ||
            bodies[0].Elements(P + "txBody").Count() > 1))
            throw new InvalidOperationException("The notes part has an ambiguous speaker notes placeholder.");
        return bodies.SingleOrDefault();
    }

    private static string? ShapePropertiesOrNull(XElement? shape) =>
        shape is null ? null : ShapeProperties(shape)?.ToString(SaveOptions.DisableFormatting);

    private void CheckNotesOwner(string slide, string notes)
    {
        if (!string.Equals(FindRelatedPart(notes, "slide"), slide, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The notes part does not belong to the slide.");
    }
}
