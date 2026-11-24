using System.Security.Cryptography;
using System.Xml.Linq;

namespace CellBridge.Client.PowerPoint;

internal sealed partial class PowerPointPackage
{
    internal string SelectLayout(uint? layoutFromSlideId)
    {
        if (layoutFromSlideId is uint template)
        {
            var layout = FindRelatedPart(GetSlide(template).Part, "slideLayout");
            return layout is not null && HasLayout(layout) ? layout
                : throw new InvalidOperationException($"Slide {template} has no registered slide layout.");
        }

        var layouts = RegisteredLayouts().ToArray();
        return layouts.FirstOrDefault(layout => (string?)Root(layout, P + "sldLayout").Attribute("type") == "blank")
            ?? layouts.FirstOrDefault()
            ?? throw new InvalidOperationException("The presentation has no available slide layout.");
    }

    internal bool HasLayout(string layout) => RegisteredLayouts().Contains(layout, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<string> RegisteredLayouts()
    {
        foreach (var master in _presentation.Element(P + "sldMasterIdLst")?.Elements(P + "sldMasterId") ?? [])
        {
            var masterPart = FindReferencedPart(PresentationPart, master, "slideMaster");
            var root = Root(masterPart, P + "sldMaster");
            foreach (var layout in root.Element(P + "sldLayoutIdLst")?.Elements(P + "sldLayoutId") ?? [])
            {
                var part = FindReferencedPart(masterPart, layout, "slideLayout");
                if (!string.Equals(FindRelatedPart(part, "slideMaster"), masterPart, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A slide layout does not refer back to its registered master.");
                yield return part;
            }
        }
    }

    internal uint AddSlide(string layout)
    {
        var id = _slides.Keys.DefaultIfEmpty(255u).Max() + 1;
        if (id >= 2147483648)
        {
            id = 256;
            while (_slides.ContainsKey(id))
                id++;
        }

        var slide = NextPart("slides/slide");
        var commonSlideData = new XElement(P + "cSld", EmptyShapeTree(),
            new XElement(P + "extLst",
                new XElement(P + "ext", new XAttribute("uri", "{BB962C8B-B14F-4D97-AF65-F5344CB8AC3E}"),
                    new XElement(P14 + "creationId", new XAttribute("val", RandomNumberGenerator.GetInt32(1, int.MaxValue))))));
        var root = PresentationRoot("sld", commonSlideData, ColorMapOverride());
        root.Add(new XAttribute(XNamespace.Xmlns + "p14", P14), new XAttribute(XNamespace.Xmlns + "mc", Mc),
            new XAttribute(Mc + "Ignorable", "p14"));
        AddPart(slide, root);
        AddRelationship(slide, layout, "slideLayout");
        AddContentType(slide, "slide");
        var relationship = AddRelationship(PresentationPart, slide, "slide");
        var list = PresentationList("sldIdLst", "sldMasterIdLst", "notesMasterIdLst", "handoutMasterIdLst");
        list.Add(new XElement(P + "sldId", new XAttribute("id", id), new XAttribute(R + "id", relationship)));
        Change(PresentationPart);
        return id;
    }

    internal void DeleteSlide(uint id)
    {
        var slide = GetSlide(id);
        var notes = FindRelatedPart(slide.Part, "notesSlide");
        if (notes is not null)
            CheckNotesOwner(slide.Part, notes);
        var relationshipId = Required(slide.Entry, R + "id");
        if (_presentation.Descendants().Any(element => element != slide.Entry &&
            (element.Attributes().Any(attribute => attribute.Name.Namespace == R && attribute.Value == relationshipId) ||
             element.Name.LocalName == "sldId" && element.Parent != slide.Entry.Parent &&
             (uint?)element.Attribute("id") == id)))
            throw new InvalidOperationException("The slide belongs to a section, custom show or another presentation reference.");

        var presentationRelationship = Relationships(PresentationPart).Single(relationship =>
            Required(relationship, "Id") == relationshipId);
        foreach (var name in _entriesByName.Keys.Where(name => name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            var marker = name.LastIndexOf("_rels/", StringComparison.OrdinalIgnoreCase);
            if (marker < 0 || marker > 0 && name[marker - 1] != '/')
                continue;
            var source = name[..marker] + name[(marker + 6)..^5];
            if (string.Equals(source, slide.Part, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, notes, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var relationship in Relationships(source))
            {
                if (relationship == presentationRelationship || IsExternal(relationship))
                    continue;
                var target = ResolveTarget(source, relationship);
                if (string.Equals(target, slide.Part, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target, notes, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"'{source}' references the slide or its notes.");
            }
        }

        var list = slide.Entry.Parent!;
        slide.Entry.Remove();
        if (!list.Elements().Any())
            list.Remove();
        presentationRelationship.Remove();
        Change(PresentationPart);
        Change(_presentationRelationships);
        RemovePart(slide.Part);
        if (notes is not null)
            RemovePart(notes);
    }

    internal string SlideFingerprint(uint id)
    {
        var slide = GetSlide(id);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new Stack<string>();
        paths.Push(slide.Part);
        while (paths.TryPop(out var path))
        {
            if (!visited.Add(path))
                continue;
            var relationships = RelationshipsPath(path);
            if (_zip.Contains(relationships))
                visited.Add(relationships);
            foreach (var relationship in Relationships(path))
            {
                if (IsExternal(relationship) || HasType(relationship, "slide") ||
                    HasType(relationship, "slideLayout") || HasType(relationship, "notesMaster"))
                    continue;
                paths.Push(ExistingTarget(path, relationship));
            }
        }

        return string.Join("\n", visited.Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => path + ":" + PartFingerprint(path)));
    }

    internal string LayoutFingerprint(string layout) =>
        PartFingerprint(layout) + PartFingerprint(RelationshipsPath(layout));

    private string PartFingerprint(string path)
    {
        using var stream = _entriesByName[path].Open();
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private string NextPart(string relativePrefix)
    {
        var prefix = PresentationPart[..(PresentationPart.LastIndexOf('/') + 1)] + relativePrefix;
        for (var index = 1; ; index++)
        {
            var part = $"{prefix}{index}.xml";
            if (!_zip.Contains(part) && !_loaded.ContainsKey(part) &&
                !_zip.Contains(RelationshipsPath(part)) && !_loaded.ContainsKey(RelationshipsPath(part)) &&
                FindContentType(part) is null)
                return part;
        }
    }

    private string AddRelationship(string source, string target, string type)
    {
        var path = RelationshipsPath(source);
        var ids = Relationships(source).Select(relationship => Required(relationship, "Id"))
            .ToHashSet(StringComparer.Ordinal);
        var suffix = 1;
        while (ids.Contains($"rId{suffix}"))
            suffix++;
        var id = $"rId{suffix}";
        if (!_zip.Contains(path) && !_loaded.ContainsKey(path))
            AddPart(path, new XElement(Rel + "Relationships"));
        var origin = new Uri("https://package.invalid/" + source);
        var destination = new Uri("https://package.invalid/" + target);
        Root(path, Rel + "Relationships").Add(new XElement(Rel + "Relationship", new XAttribute("Id", id),
            new XAttribute("Type", R.NamespaceName + "/" + type),
            new XAttribute("Target", origin.MakeRelativeUri(destination).OriginalString)));
        Change(path);
        return id;
    }

    private XElement? FindContentType(string path) =>
        Root(ContentTypes, Ct + "Types").Elements(Ct + "Override").SingleOrDefault(element =>
            string.Equals((string?)element.Attribute("PartName"), "/" + path, StringComparison.OrdinalIgnoreCase));

    private void AddContentType(string path, string kind)
    {
        Root(ContentTypes, Ct + "Types").Add(new XElement(Ct + "Override", new XAttribute("PartName", "/" + path),
            new XAttribute("ContentType", PowerPointContentType + kind + "+xml")));
        Change(ContentTypes);
    }

    private void RemovePart(string path)
    {
        _removed.Add(path);
        var relationships = RelationshipsPath(path);
        if (_zip.Contains(relationships))
            _removed.Add(relationships);
        if (FindContentType(path) is { } contentType)
        {
            contentType.Remove();
            Change(ContentTypes);
        }
    }

    private XElement PresentationList(string name, params string[] predecessors)
    {
        if (_presentation.Element(P + name) is { } existing)
            return existing;
        var list = new XElement(P + name);
        var previous = _presentation.Elements().LastOrDefault(element => element.Name.Namespace == P &&
            predecessors.Contains(element.Name.LocalName, StringComparer.Ordinal));
        if (previous is null)
            _presentation.AddFirst(list);
        else
            previous.AddAfterSelf(list);
        return list;
    }

    private static XElement PresentationRoot(string name, params object[] children) => new(P + name,
        new XAttribute(XNamespace.Xmlns + "p", P), new XAttribute(XNamespace.Xmlns + "a", A),
        new XAttribute(XNamespace.Xmlns + "r", R), children);

    private static XElement EmptyShapeTree() => new(P + "spTree",
        new XElement(P + "nvGrpSpPr",
            new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")),
            new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")), new XElement(P + "grpSpPr"));

    private static XElement ColorMapOverride() => new(P + "clrMapOvr", new XElement(A + "masterClrMapping"));
}
