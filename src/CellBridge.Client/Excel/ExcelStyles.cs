using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static CellBridge.Client.Excel.ExcelPackage;

namespace CellBridge.Client.Excel;

internal sealed class ExcelStyles(XElement? root, Func<string> themeSignature)
{
    private readonly XElement[] _cells = Items(root, "cellXfs");
    private readonly XElement[] _bases = Items(root, "cellStyleXfs");
    private readonly XElement[] _fonts = Items(root, "fonts");
    private readonly XElement[] _fills = Items(root, "fills");
    private readonly XElement[] _borders = Items(root, "borders");
    private readonly Dictionary<uint, string> _formats = Items(root, "numFmts").ToDictionary(
        format => (uint?)format.Attribute("numFmtId") ?? 0, format => Required(format, "formatCode"));
    private readonly XElement? _colors = root?.Element(S + "colors");
    private readonly Dictionary<uint, string> _signatures = [];

    private static XElement[] Items(XElement? root, string collection) => root?.Element(S + collection)?.Elements().ToArray() ?? [];

    private XElement? Cell(uint index) => At(_cells, index);
    private XElement? Base(XElement? cell) => At(_bases, Index(cell, "xfId"));
    private static XElement? At(XElement[] collection, uint index) => index < collection.Length ? collection[index] : null;
    private static uint Index(XElement? cell, string attribute) => (uint?)cell?.Attribute(attribute) ?? 0;
    private static bool Applied(XElement? cell, string attribute) => (string?)cell?.Attribute(attribute) is not ("0" or "false");

    internal bool IsLocked(uint index)
    {
        var cell = Cell(index);
        var protection = (Applied(cell, "applyProtection") ? cell?.Element(S + "protection") : null)
            ?? Base(cell)?.Element(S + "protection");
        return (string?)protection?.Attribute("locked") is not ("0" or "false");
    }

    internal bool IsDate(uint index)
    {
        var cell = Cell(index);
        var id = Index(Effective(cell, "applyNumberFormat", "numFmtId"), "numFmtId");
        if (_formats.TryGetValue(id, out var format))
        {
            var tokens = Regex.Replace(format, "\"[^\"]*\"|\\\\.|_.|\\*.|\\[(?![hms]+\\])[^\\]]*\\]", "", RegexOptions.IgnoreCase);
            return tokens.IndexOfAny(['d', 'D', 'y', 'Y', 'm', 'M', 'h', 'H', 's', 'S']) >= 0;
        }
        return id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58;
    }

    internal string Signature(uint index)
    {
        if (_signatures.TryGetValue(index, out var signature))
            return signature;
        var cell = Cell(index);
        var format = Index(Effective(cell, "applyNumberFormat", "numFmtId"), "numFmtId");
        var builder = new StringBuilder();
        Append(builder, _formats.GetValueOrDefault(format) ?? $"builtin:{format}");
        var components = new[]
        {
            At(_fonts, Index(Effective(cell, "applyFont", "fontId"), "fontId")),
            At(_fills, Index(Effective(cell, "applyFill", "fillId"), "fillId")),
            At(_borders, Index(Effective(cell, "applyBorder", "borderId"), "borderId")),
            Effective(cell, "applyAlignment", S + "alignment")?.Element(S + "alignment"),
            Effective(cell, "applyProtection", S + "protection")?.Element(S + "protection"),
            _colors
        };
        foreach (var component in components)
            Canonical(builder, component);
        if (components.Any(component => component?.DescendantsAndSelf().Any(element =>
                element.Attribute("theme") is not null || (element.Name == S + "scheme" &&
                (string?)element.Attribute("val") is "major" or "minor")) == true))
            Append(builder, themeSignature());
        Append(builder, (string?)cell?.Attribute("quotePrefix") ?? "0");
        Append(builder, (string?)cell?.Attribute("pivotButton") ?? "0");
        signature = builder.ToString();
        _signatures.Add(index, signature);
        return signature;
    }

    private XElement? Effective(XElement? cell, string flag, XName member) =>
        Applied(cell, flag) && (cell?.Attribute(member) is not null || cell?.Element(member) is not null) ? cell : Base(cell);

    internal static string Signature(XElement element)
    {
        var builder = new StringBuilder();
        Canonical(builder, element);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Canonical(StringBuilder builder, XElement? element)
    {
        if (element is null)
        {
            Append(builder, "");
            return;
        }
        Append(builder, element.Name.ToString());
        foreach (var attr in element.Attributes().Where(attr => !attr.IsNamespaceDeclaration)
                     .OrderBy(attr => attr.Name.ToString(), StringComparer.Ordinal))
        {
            Append(builder, attr.Name.ToString());
            Append(builder, attr.Value);
        }
        Append(builder, "");
        foreach (var node in element.Nodes())
        {
            if (node is XElement child)
                Canonical(builder, child);
            else if (node is XText text && (!string.IsNullOrWhiteSpace(text.Value) || !element.HasElements))
                Append(builder, text.Value);
        }
        Append(builder, "");
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
}
