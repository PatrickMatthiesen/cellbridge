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

    internal static string ReferencePattern(string formula) =>
        RewriteReferences(formula, 0, 0, ReferenceRewrite.Pattern);

    private const string CellReferenceMarker = "\0Cell\0";

    private static string RewriteReferences(string formula, int rowOffset, int columnOffset, ReferenceRewrite rewrite)
    {
        return ReferenceToken().Replace(formula, match =>
        {
            if (match.Groups["reference"].Success)
                return RewriteCell(match.Value, rowOffset, columnOffset, rewrite);
            if (match.Groups["broken"].Success && rewrite == ReferenceRewrite.Pattern)
                return CellReferenceMarker;
            if (match.Groups["rows"].Success)
                return RewriteRange(match.Value, rowOffset, row: true, rewrite);
            if (match.Groups["columns"].Success)
                return RewriteRange(match.Value, columnOffset, row: false, rewrite);
            return match.Value;
        });
    }

    private static string RewriteCell(string text, int rowOffset, int columnOffset, ReferenceRewrite rewrite)
    {
        var columnStart = text[0] == '$' ? 1 : 0;
        var separator = columnStart;
        while (separator < text.Length && char.IsAsciiLetter(text[separator]))
            separator++;
        var rowStart = text[separator] == '$' ? separator + 1 : separator;
        if (ColumnNumber(text.AsSpan(columnStart, separator - columnStart)) is < 1 or > CellAddress.LastColumn ||
            int.Parse(text.AsSpan(rowStart), CultureInfo.InvariantCulture) is < 1 or > CellAddress.LastRow)
            return text;

        if (rewrite == ReferenceRewrite.Pattern)
            return CellReferenceMarker;
        var column = RewriteAxis(text[..separator], columnOffset, row: false, rewrite);
        var row = RewriteAxis(text[separator..], rowOffset, row: true, rewrite);
        return column is null || row is null ? "#REF!" : column + row;
    }

    private static string RewriteRange(string range, int offset, bool row, ReferenceRewrite rewrite)
    {
        var separator = range.IndexOf(':');
        var first = RewriteAxis(range[..separator], offset, row, rewrite);
        var last = RewriteAxis(range[(separator + 1)..], offset, row, rewrite);
        return $"{first ?? "#REF!"}:{last ?? "#REF!"}";
    }

    private static string? RewriteAxis(string axis, int offset, bool row, ReferenceRewrite rewrite)
    {
        var absolute = axis.StartsWith('$');
        if (absolute && rewrite != ReferenceRewrite.Pattern)
            return axis;
        var coordinate = axis.AsSpan(absolute ? 1 : 0);
        var number = row ? int.Parse(coordinate, CultureInfo.InvariantCulture) : ColumnNumber(coordinate);
        var limit = row ? CellAddress.LastRow : CellAddress.LastColumn;
        if (number > limit)
            return axis;
        // A NUL cannot occur in workbook XML, so it separates reference markers from literal text.
        if (rewrite == ReferenceRewrite.Pattern)
            return string.Concat(absolute ? "\0$" : "\0", row ? "R\0" : "C\0");
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
        | [\p{L}_][\p{L}\p{N}_.]*(?::[\p{L}_][\p{L}\p{N}_.]*)?\s*(?=[!(])
        | (?<![\p{L}\p{N}_.\\]) (?:
            (?<broken>\#REF!)
            | (?<reference>\$?[A-Z]{1,3}\$?[1-9][0-9]{0,6})
            | (?<rows>\$?[1-9][0-9]{0,6}:\$?[1-9][0-9]{0,6})
            | (?<columns>\$?[A-Z]{1,3}:\$?[A-Z]{1,3})
        ) (?![\p{L}\p{N}_.])
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceToken();
}
