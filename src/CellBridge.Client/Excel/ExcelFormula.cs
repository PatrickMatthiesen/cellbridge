using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace CellBridge.Client.Excel;

internal static partial class ExcelFormula
{
    private enum ReferenceRewrite { Translate, Relative, Pattern }

    // Known dynamic-array producers are excluded because their metadata and spill ranges
    // cannot be authored by this editor.
    private static readonly HashSet<string> SpillFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "FILTER", "UNIQUE", "SORT", "SORTBY", "SEQUENCE", "RANDARRAY", "TOCOL", "TOROW",
        "TAKE", "DROP", "HSTACK", "VSTACK", "WRAPROWS", "WRAPCOLS", "EXPAND", "TEXTSPLIT",
        "CHOOSECOLS", "CHOOSEROWS", "MAKEARRAY", "MAP", "SCAN", "BYROW", "BYCOL", "GROUPBY", "PIVOTBY"
    };

    private static readonly HashSet<string> FutureFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "XLOOKUP", "XMATCH", "LET", "LAMBDA", "IFS", "SWITCH", "MAXIFS", "MINIFS",
        "TEXTJOIN", "CONCAT", "TEXTBEFORE", "TEXTAFTER", "REGEXTEST", "REGEXEXTRACT", "REGEXREPLACE"
    };

    internal static void Validate(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula) || formula.Length > 8192 || formula.TrimStart().StartsWith('='))
            throw new ArgumentException("Expected invariant Excel formula text without the leading equals sign.");
        XmlConvert.VerifyXmlChars(formula);
        var delimiters = new Stack<char>();
        var quote = '\0';
        var braces = 0;
        var brackets = 0;
        for (var i = 0; i < formula.Length; i++)
        {
            var character = formula[i];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    if (i + 1 < formula.Length && formula[i + 1] == quote)
                        i++;
                    else
                        quote = '\0';
                }
                continue;
            }
            if (brackets > 0 && character == '\'' && i + 1 < formula.Length)
            {
                i++;
                continue;
            }
            if (brackets == 0 && character is '"' or '\'')
                quote = character;
            else if (character == '(' && brackets == 0)
            {
                ValidateFunction(FunctionBefore(formula, i));
                delimiters.Push(')');
            }
            else if (character == '[')
            {
                delimiters.Push(']');
                brackets++;
            }
            else if (character == ']' || (brackets == 0 && character is ')' or '}'))
            {
                if (!delimiters.TryPop(out var expected) || character != expected)
                    throw new ArgumentException("The Excel formula delimiters are unbalanced.");
                if (character == ']')
                    brackets--;
                if (character == '}')
                    braces--;
            }
            else if (brackets == 0 && character == '{')
            {
                delimiters.Push('}');
                braces++;
            }
            else if (brackets == 0 && character == ';' && braces == 0)
                throw new ArgumentException("Use commas between arguments in invariant Excel formulas.");
            else if (brackets == 0 && character == '#' && i > 0 &&
                     (char.IsAsciiDigit(formula[i - 1]) || formula[i - 1] == ')'))
                throw new ArgumentException("Spill references are outside the supported formula subset.");
        }

        if (quote != '\0' || delimiters.Count != 0)
            throw new ArgumentException("The Excel formula delimiters are unbalanced.");
    }

    private static void ValidateFunction(string token)
    {
        var pieces = token.Split('.');
        var name = pieces[^1];
        if (SpillFunctions.Contains(name) || name.Equals("ANCHORARRAY", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Dynamic-array function '{name}' is outside the supported formula subset.");
        var futurePrefix = token.StartsWith("_xlfn.", StringComparison.Ordinal);
        if ((FutureFunctions.Contains(name) && !futurePrefix) ||
            (futurePrefix && (pieces[1].Length == 0 || !char.IsAsciiLetter(pieces[1][0]) ||
                pieces.Skip(2).Any(piece => piece.Length == 0 || !char.IsAsciiLetterOrDigit(piece[0])))))
            throw new ArgumentException($"Function '{name}' has an invalid or missing _xlfn. prefix.");
    }

    private static string FunctionBefore(string formula, int delimiter)
    {
        var end = delimiter;
        while (end > 0 && char.IsWhiteSpace(formula[end - 1]))
            end--;
        var start = end;
        while (start > 0 && (char.IsAsciiLetterOrDigit(formula[start - 1]) || formula[start - 1] is '_' or '.'))
            start--;
        return formula[start..end];
    }

    internal static string Translate(string formula, CellAddress master, CellAddress target)
    {
        if (formula.Length > 8192)
            throw new InvalidDataException("The shared formula exceeds Excel's formula length limit.");
        return RewriteReferences(formula, target.Row - master.Row, target.Column - master.Column, ReferenceRewrite.Translate);
    }

    internal static string RelativeSignature(string formula, CellAddress address) =>
        RewriteReferences(formula, -address.Row, -address.Column, ReferenceRewrite.Relative);

    internal static string ReferencePattern(string formula, bool? fixedRow = null) =>
        RewriteReferences(formula, 0, 0, ReferenceRewrite.Pattern, fixedRow);

    private const string ReferenceMarker = "\0Ref\0";
    private const int CoordinateLength = 7;
    private const int ReferenceFieldsLength = 3 + 2 * CoordinateLength;

    // After the marker, a token stores the reference kind, two anchor flags,
    // and two coordinates on the axis that the formula cell did not move along.
    internal static bool HasBrokenReference(string pattern)
    {
        int search = 0;
        while (pattern.IndexOf(ReferenceMarker, search, StringComparison.Ordinal) is var marker && marker >= 0)
        {
            int kind = marker + ReferenceMarker.Length;
            if (pattern.AsSpan(kind, 3).Contains('?'))
                return true;
            search = kind + ReferenceFieldsLength;
        }
        return false;
    }

    // Both patterns have the same shape. A broken endpoint can match any
    // anchor or coordinate, but a surviving endpoint still has to match.
    internal static bool CompatibleReferences(string left, string right)
    {
        int search = 0;
        while (left.IndexOf(ReferenceMarker, search, StringComparison.Ordinal) is var marker && marker >= 0)
        {
            int kind = marker + ReferenceMarker.Length;
            for (int i = kind; i < kind + 3; i++)
            {
                if (left[i] != '?' && right[i] != '?' && left[i] != right[i])
                    return false;
            }
            if (left[kind] != '?' && right[kind] != '?')
            {
                for (int endpoint = 0; endpoint < 2; endpoint++)
                {
                    int anchor = kind + 1 + endpoint;
                    int coordinate = kind + 3 + endpoint * CoordinateLength;
                    if (left[anchor] != '?' && right[anchor] != '?' &&
                        !left.AsSpan(coordinate, CoordinateLength).SequenceEqual(right.AsSpan(coordinate, CoordinateLength)))
                        return false;
                }
            }
            search = kind + ReferenceFieldsLength;
        }
        return true;
    }

    private static string RewriteReferences(string formula, int rowOffset, int columnOffset, ReferenceRewrite rewrite,
        bool? fixedRow = null)
    {
        return ReferenceToken().Replace(formula, match =>
        {
            if (match.Groups["colon"].Success)
            {
                var kind = match.Groups["rows"].Success ? 'R' : match.Groups["columns"].Success ? 'C' : 'B';
                return RewriteRange(match, kind, rowOffset, columnOffset, rewrite, fixedRow);
            }
            if (match.Groups["reference"].Success)
                return RewriteCell(match.Value, rowOffset, columnOffset, rewrite, fixedRow);
            if (match.Groups["broken"].Success && rewrite == ReferenceRewrite.Pattern)
                return fixedRow is null ? ReferenceMarker : PatternToken('?', '?', '?', 0);
            return match.Value;
        });
    }

    private static string RewriteCell(string text, int rowOffset, int columnOffset, ReferenceRewrite rewrite, bool? fixedRow)
    {
        if (!TryCellAxes(text, out var separator, out var rowStart, out var address))
            return text;
        if (rewrite == ReferenceRewrite.Pattern)
            return fixedRow is { } rowIsFixed
                ? PatternToken('A', text[0] == '$' ? '$' : '-', rowStart != separator ? '$' : '-',
                    rowIsFixed ? address.Row : address.Column)
                : ReferenceMarker;
        var column = RewriteAxis(text[..separator], columnOffset, row: false, rewrite);
        var row = RewriteAxis(text[separator..], rowOffset, row: true, rewrite);
        return column is null || row is null ? "#REF!" : column + row;
    }

    private static bool TryCellAxes(string text, out int separator, out int rowStart, out CellAddress address)
    {
        var columnStart = text[0] == '$' ? 1 : 0;
        separator = columnStart;
        while (separator < text.Length && char.IsAsciiLetter(text[separator]))
            separator++;
        rowStart = text[separator] == '$' ? separator + 1 : separator;
        var column = ColumnNumber(text.AsSpan(columnStart, separator - columnStart));
        var row = int.Parse(text.AsSpan(rowStart), CultureInfo.InvariantCulture);
        address = new CellAddress(row, column);
        return column is >= 1 and <= CellAddress.LastColumn && row is >= 1 and <= CellAddress.LastRow;
    }

    private static string RewriteRange(Match match, char kind, int rowOffset, int columnOffset,
        ReferenceRewrite rewrite, bool? fixedRow)
    {
        var colon = match.Groups["colon"];
        var separator = colon.Index - match.Index;
        var range = match.Value;
        var first = range[..separator];
        var last = range[(separator + colon.Length)..];
        if (rewrite == ReferenceRewrite.Pattern)
        {
            var firstReference = RangeReference(first, kind, fixedRow);
            var lastReference = RangeReference(last, kind, fixedRow);
            if (firstReference is not { } start || lastReference is not { } end)
                return range;
            return fixedRow is null ? ReferenceMarker : PatternToken(kind, start.Anchor, end.Anchor, start.Coordinate, end.Coordinate);
        }
        return RewriteEndpoint(first) + colon.Value + RewriteEndpoint(last);

        string RewriteEndpoint(string endpoint)
        {
            if (endpoint[0] == '#')
                return endpoint;
            if (kind == 'B')
                return RewriteCell(endpoint, rowOffset, columnOffset, rewrite, fixedRow);
            var row = kind == 'R';
            return RewriteAxis(endpoint, row ? rowOffset : columnOffset, row, rewrite) ?? "#REF!";
        }
    }

    private static (char Anchor, int Coordinate)? RangeReference(string endpoint, char kind, bool? fixedRow)
    {
        if (kind != 'B')
        {
            var axis = endpoint.AsSpan(endpoint[0] == '$' ? 1 : 0);
            var row = kind == 'R';
            var coordinate = row ? int.Parse(axis, CultureInfo.InvariantCulture) : ColumnNumber(axis);
            if (coordinate > (row ? CellAddress.LastRow : CellAddress.LastColumn))
                return null;
            return (endpoint[0] == '$' ? '$' : '-', fixedRow == row ? coordinate : 0);
        }
        if (endpoint[0] == '#')
            return ('?', 0);
        if (!TryCellAxes(endpoint, out var columnEnd, out var rowStart, out var address))
            return null;
        var anchor = (char)('0' + (endpoint[0] == '$' ? 2 : 0) + (rowStart != columnEnd ? 1 : 0));
        var fixedCoordinate = fixedRow is { } rowIsFixed ? (rowIsFixed ? address.Row : address.Column) : 0;
        return (anchor, fixedCoordinate);
    }

    private static string PatternToken(char kind, char firstAnchor, char secondAnchor, int firstCoordinate, int secondCoordinate = 0) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{ReferenceMarker}{kind}{firstAnchor}{secondAnchor}{firstCoordinate:D7}{secondCoordinate:D7}");

    private static string? RewriteAxis(string axis, int offset, bool row, ReferenceRewrite rewrite)
    {
        var absolute = axis.StartsWith('$');
        if (absolute)
            return axis;
        var coordinate = axis.AsSpan(absolute ? 1 : 0);
        var number = row ? int.Parse(coordinate, CultureInfo.InvariantCulture) : ColumnNumber(coordinate);
        var limit = row ? CellAddress.LastRow : CellAddress.LastColumn;
        if (number > limit)
            return axis;
        // A NUL cannot occur in workbook XML, so it separates reference markers from literal text.
        number += offset;
        if (rewrite == ReferenceRewrite.Relative)
            return string.Concat(row ? "\0R" : "\0C", number.ToString(CultureInfo.InvariantCulture), "\0");
        if (number < 1 || number > limit)
            return null;
        return row ? number.ToString(CultureInfo.InvariantCulture) : new CellAddress(1, number).ToString()[..^1];
    }

    private static int ColumnNumber(ReadOnlySpan<char> axis)
    {
        var column = 0;
        foreach (var letter in axis)
            column = column * 26 + char.ToUpperInvariant(letter) - 'A' + 1;
        return column;
    }

    [GeneratedRegex("""
        "(?:[^"]|"")*" | '(?:[^']|'')*' | \[(?:'['\[\]#@]|[^\[\]]|\[(?:'['\[\]#@]|[^\[\]])*\])*\]
        | [\p{L}_][\p{L}\p{N}_.]*(?:\s*:\s*[\p{L}_][\p{L}\p{N}_.]*)?\s*(?=[!(])
        | (?<![\p{L}\p{N}_.\\]) (?:
            (?:\$?[A-Z]{1,3}\$?[1-9][0-9]{0,6}|\#REF!) (?<colon>\s*:\s*) (?:\$?[A-Z]{1,3}\$?[1-9][0-9]{0,6}|\#REF!)
            | (?<broken>\#REF!)
            | (?<reference>\$?[A-Z]{1,3}\$?[1-9][0-9]{0,6})
            | (?<rows>\$?[1-9][0-9]{0,6}(?<colon>\s*:\s*)\$?[1-9][0-9]{0,6})
            | (?<columns>\$?[A-Z]{1,3}(?<colon>\s*:\s*)\$?[A-Z]{1,3})
        ) (?![\p{L}\p{N}_.])
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceToken();
}
