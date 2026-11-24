using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace CellBridge.Client.Word;

internal sealed class WordParagraphs
{
    private readonly XElement _body;
    private readonly XNamespace _w;
    private readonly Dictionary<string, int> _indices;
    private Dictionary<string, XElement>? _styles;
    private string? _defaultStyle;
    private XElement? _defaultProperties;

    internal WordParagraphs(WordPackage package)
    {
        _body = package.Body;
        _w = package.W;
        Elements = package.Body.Elements(package.W + "p").ToArray();
        var allIds = package.Body.Descendants(package.W + "p").Select(WordPackage.StableId).OfType<string>()
            .GroupBy(id => id).Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        Ids = Elements.Select(element => WordPackage.StableId(element) is { } id && allIds.Contains(id) ? id : null).ToArray();
        _indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Elements.Length; i++)
        {
            if (Ids[i] is { } id)
                _indices.Add(id, i);
        }
        InitializeStyles(package.ReadStyles());
    }

    internal XElement[] Elements { get; }
    internal string?[] Ids { get; }
    internal WordParagraphReference Reference(int index) => new(index, Ids[index]);

    internal int? Resolve(WordParagraphReference reference)
    {
        if (reference.Id is { } id)
            return _indices.TryGetValue(id.ToUpperInvariant(), out var index) ? index : null;
        return (uint)reference.Index < (uint)Elements.Length ? reference.Index : null;
    }

    internal int?[] MapTo(WordParagraphs latest, bool matchByMarkup)
    {
        var result = new int?[Elements.Length];
        if (XNode.DeepEquals(_body, latest._body))
        {
            for (var i = 0; i < Elements.Length; i++)
                result[i] = i;
            return result;
        }

        for (var i = 0; i < Elements.Length; i++)
        {
            if (Ids[i] is { } id && latest._indices.TryGetValue(id, out var index))
                result[i] = index;
        }
        if (!matchByMarkup || !Ids.Contains(null))
            return result;

        var matched = new bool[latest.Elements.Length];
        foreach (var position in result)
            if (position is { } index)
                matched[index] = true;

        var originalKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        var latestKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Elements.Length; i++)
            AddKey(originalKeys, Elements[i], i);
        for (var i = 0; i < latest.Elements.Length; i++)
            AddKey(latestKeys, latest.Elements[i], i);
        foreach (var (key, original) in originalKeys)
        {
            // For diffs, match unique unchanged markup against unclaimed paragraphs.
            // An ID-less paragraph may acquire an ID when Word saves the document.
            if (original >= 0 && Ids[original] is null && latestKeys.TryGetValue(key, out var current) && current >= 0 && !matched[current])
            {
                result[original] = current;
                matched[current] = true;
            }
        }

        // If content disappeared and appeared, a shifted text match could be a different
        // paragraph edited to contain the same text. Require stable IDs for that case.
        var unmatchedOnBothSides = result.Contains(null) && matched.Contains(false);
        var reordered = Reordered(result);
        for (var i = 0; i < result.Length; i++)
            if (Ids[i] is null && result[i] is { } position && (reordered[i] || (position != i && unmatchedOnBothSides)))
                result[i] = null;
        return result;
    }

    internal static bool[] Reordered(int?[] map)
    {
        var reordered = new bool[map.Length];
        var maximum = -1;
        for (var i = 0; i < map.Length; i++)
        {
            if (map[i] is not { } position)
                continue;
            reordered[i] = position < maximum;
            maximum = Math.Max(maximum, position);
        }
        var minimum = int.MaxValue;
        for (var i = map.Length - 1; i >= 0; i--)
        {
            if (map[i] is not { } position)
                continue;
            reordered[i] |= position > minimum;
            minimum = Math.Min(minimum, position);
        }
        return reordered;
    }

    private static void AddKey(Dictionary<string, int> keys, XElement paragraph, int index)
    {
        var key = ComparableMarkup(paragraph).ToString(SaveOptions.DisableFormatting);
        if (!keys.TryAdd(key, index))
            keys[key] = -1;
    }

    internal static bool SameMarkup(XElement before, XElement after) => XNode.DeepEquals(before, after) ||
        XNode.DeepEquals(ComparableMarkup(before), ComparableMarkup(after));

    private static XElement ComparableMarkup(XElement paragraph)
    {
        var copy = new XElement(paragraph);
        foreach (var attribute in copy.Attributes().Where(attribute => attribute.Name == WordPackage.ParagraphId ||
                     attribute.Name == WordPackage.ParagraphId.Namespace + "textId" ||
                     (attribute.Name.Namespace == paragraph.Name.Namespace && attribute.Name.LocalName.StartsWith("rsid", StringComparison.Ordinal))).ToArray())
            attribute.Remove();
        return copy;
    }

    internal WordParagraph Read(int index)
    {
        var paragraph = Elements[index];
        var pPr = paragraph.Element(_w + "pPr");
        var styleId = (string?)pPr?.Element(_w + "pStyle")?.Attribute(_w + "val");
        var text = new StringBuilder();
        AppendText(text, paragraph);
        return new WordParagraph(Reference(index), text.ToString(), styleId, HeadingLevel(pPr, styleId));
    }

    internal int? HeadingLevel(int index)
    {
        var pPr = Elements[index].Element(_w + "pPr");
        return HeadingLevel(pPr, (string?)pPr?.Element(_w + "pStyle")?.Attribute(_w + "val"));
    }

    private int? HeadingLevel(XElement? properties, string? styleId)
    {
        var level = OutlineLevel(properties);
        if (level is not null)
            return level <= 9 ? level : null;
        var id = styleId ?? _defaultStyle;
        if (id is null)
            return OutlineLevel(_defaultProperties) is <= 9 and { } defaultLevel ? defaultLevel : null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (id is not null && visited.Add(id))
        {
            if (!_styles!.TryGetValue(id, out var style))
            {
                if (id.Length == 8 && id.StartsWith("Heading", StringComparison.Ordinal) && id[7] is >= '1' and <= '9')
                    return id[7] - '0';
                break;
            }
            level = OutlineLevel(style.Element(_w + "pPr"));
            if (level is not null)
                return level <= 9 ? level : null;
            id = (string?)style.Element(_w + "basedOn")?.Attribute(_w + "val");
        }
        level = OutlineLevel(_defaultProperties);
        return level <= 9 ? level : null;
    }

    private int? OutlineLevel(XElement? properties) => int.TryParse(
        (string?)properties?.Element(_w + "outlineLvl")?.Attribute(_w + "val"), CultureInfo.InvariantCulture,
        out var level) && level is >= 0 and <= 9 ? level + 1 : null;

    private void InitializeStyles(XElement? root)
    {
        var w = _w;
        _styles = new Dictionary<string, XElement>(StringComparer.Ordinal);
        if (root is null)
            return;
        _defaultProperties = root.Element(w + "docDefaults")?.Element(w + "pPrDefault")?.Element(w + "pPr");
        foreach (var style in root.Elements(w + "style"))
        {
            if ((string?)style.Attribute(w + "type") != "paragraph" || (string?)style.Attribute(w + "styleId") is not { } id)
                continue;
            if (!_styles.TryAdd(id, style))
                throw new InvalidDataException("The Word document contains duplicate paragraph styles.");
            if ((string?)style.Attribute(w + "default") is "1" or "true" or "on")
                _defaultStyle = id;
        }
    }

    private void AppendText(StringBuilder text, XElement parent)
    {
        var w = _w;
        foreach (var node in parent.Elements())
        {
            if (node.Name == w + "t")
                text.Append(node.Value);
            else if (node.Name == w + "tab")
                text.Append('\t');
            else if (node.Name == w + "br" || node.Name == w + "cr")
                text.Append('\n');
            else if (node.Name == w + "noBreakHyphen")
                text.Append('\u2011');
            else if (node.Name == w + "softHyphen")
                text.Append('\u00AD');
            else if (node.Name == WordPackage.Mc + "AlternateContent")
            {
                if ((node.Element(WordPackage.Mc + "Fallback") ?? node.Element(WordPackage.Mc + "Choice")) is { } branch)
                    AppendText(text, branch);
            }
            else if (node.Name != w + "del" && node.Name != w + "moveFrom" && node.Name != w + "p" &&
                     node.Name != w + "pPr" && node.Name != w + "rPr" && node.Name != w + "txbxContent")
                AppendText(text, node);
        }
    }
}
