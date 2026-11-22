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

    private enum TokenKind { Name, Literal, Error, Sheet, RowRange, ColumnRange, StructuredReference, Symbol }
    private enum Expression { Empty, Incomplete, Sheet, Range, Value, Reference }
    private enum Group { Root, Parentheses, Function }

    private readonly record struct Token(TokenKind Kind, int Start, int End);
    private readonly record struct Frame(Group Group, bool RequiresReference, Expression FunctionResult,
        bool OuterValueOperator, string? FunctionName = null, int ArgumentSeparators = 0);

    // These calls can return references usable by Excel's reference operators.
    private static readonly HashSet<string> ReferenceFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHOOSE", "IF", "INDEX", "INDIRECT", "OFFSET", "_xlfn.XLOOKUP"
    };

    internal static void Validate(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula) || formula.Length > 8192 || formula.TrimStart().StartsWith('='))
            throw new ArgumentException("Expected invariant Excel formula text without the leading equals sign.");
        XmlConvert.VerifyXmlChars(formula);

        var groups = new Stack<Frame>();
        groups.Push(new Frame(Group.Root, false, Expression.Empty, false));
        var expression = Expression.Empty;
        var beforePrevious = Expression.Empty;
        var valueOperator = false;
        var previous = new Token(TokenKind.Symbol, 0, 0);
        using var tokens = Tokens(formula).GetEnumerator();
        while (tokens.MoveNext())
        {
            var token = tokens.Current;
            var separated = previous.End < token.Start;
            var frame = groups.Peek();
            var group = frame.Group;
            var symbol = formula[token.Start];
            var beforeToken = expression;
            var hasOperand = expression is Expression.Value or Expression.Reference;
            if (token.Kind != TokenKind.Symbol)
            {
                var operand = formula.AsSpan(token.Start, token.End - token.Start);
                if ((token.Kind is TokenKind.RowRange or TokenKind.ColumnRange) &&
                    !IsGridRange(operand, token.Kind == TokenKind.RowRange) ||
                    token.Kind == TokenKind.Name && operand.Contains('$') && !TryCellAxes(operand, out _, out _, out _))
                    throw InvalidExpression();

                var isReference = token.Kind switch
                {
                    TokenKind.Literal => false,
                    TokenKind.Error => operand.Equals("#REF!", StringComparison.OrdinalIgnoreCase),
                    TokenKind.Name => !IsBoolean(operand),
                    _ => true
                };
                var attachesToName = token.Kind == TokenKind.StructuredReference &&
                    previous.Kind == TokenKind.Name && !separated;
                if (hasOperand && !attachesToName && !(separated && expression == Expression.Reference && isReference) ||
                    expression == Expression.Sheet && token.Kind == TokenKind.Sheet ||
                    expression is Expression.Sheet or Expression.Range && !isReference)
                    throw InvalidExpression();
                expression = token.Kind == TokenKind.Sheet ? Expression.Sheet :
                    isReference ? Expression.Reference : Expression.Value;
            }
            else if (symbol == '{')
            {
                if (expression is Expression.Sheet or Expression.Range || hasOperand)
                    throw InvalidExpression();
                ValidateArrayConstant(formula, tokens);
                token = tokens.Current;
                expression = Expression.Value;
            }
            else if (symbol == '(')
            {
                var hasFunctionName = previous.Kind == TokenKind.Name && hasOperand;
                var invokesResult = !separated && hasOperand && formula[previous.Start] == ')';
                var opensFunction = hasFunctionName || invokesResult;
                string? functionName = null;
                var functionResult = Expression.Value;
                if (hasFunctionName)
                {
                    functionName = formula[previous.Start..previous.End];
                    ValidateFunction(functionName);
                    if (ReferenceFunctions.Contains(functionName))
                        functionResult = Expression.Reference;
                }
                if (!opensFunction && (expression == Expression.Sheet ||
                    hasOperand && !(separated && expression == Expression.Reference)))
                    throw InvalidExpression();
                var preceding = opensFunction ? beforePrevious : expression;
                groups.Push(new Frame(opensFunction ? Group.Function : Group.Parentheses,
                    preceding is Expression.Reference or Expression.Range or Expression.Sheet, functionResult,
                    valueOperator, functionName));
                expression = Expression.Empty;
                valueOperator = false;
            }
            else if (symbol is ')' or '}')
            {
                if (group == Group.Root || symbol == '}')
                    throw new ArgumentException("The Excel formula delimiters are unbalanced.");
                if (!hasOperand && !(group == Group.Function && expression == Expression.Empty))
                    throw InvalidExpression();
                if (group == Group.Function)
                {
                    // With a separator, both sides count even when an argument is omitted.
                    var argumentCount = hasOperand || frame.ArgumentSeparators > 0 ? frame.ArgumentSeparators + 1 : 0;
                    ValidateFunctionArity(frame.FunctionName, argumentCount);
                }
                groups.Pop();
                expression = group == Group.Function ? frame.FunctionResult : valueOperator ? Expression.Value : expression;
                if (frame.RequiresReference && expression != Expression.Reference)
                    throw InvalidExpression();
                valueOperator = frame.OuterValueOperator;
            }
            else if (symbol == ';')
            {
                throw new ArgumentException("Use commas between arguments in invariant Excel formulas.");
            }
            else if (symbol == ',')
            {
                if (!hasOperand && !(group == Group.Function && expression == Expression.Empty))
                    throw InvalidExpression();
                if (group == Group.Function)
                {
                    groups.Pop();
                    groups.Push(frame with { ArgumentSeparators = frame.ArgumentSeparators + 1 });
                }
                else
                    valueOperator |= expression == Expression.Value;
                expression = group == Group.Function ? Expression.Empty : Expression.Incomplete;
            }
            else if (symbol == '%')
            {
                if (!hasOperand)
                    throw InvalidExpression();
                expression = Expression.Value;
                valueOperator = true;
            }
            else if (symbol is '+' or '-' or '@')
            {
                if (expression is Expression.Sheet or Expression.Range || symbol == '@' && hasOperand)
                    throw InvalidExpression();
                expression = Expression.Incomplete;
                valueOperator = true;
            }
            else
            {
                if (!hasOperand || symbol is not ('*' or '/' or '^' or '&' or '=' or '<' or '>' or ':') ||
                    symbol == ':' && expression != Expression.Reference)
                    throw InvalidExpression();
                expression = symbol == ':' ? Expression.Range : Expression.Incomplete;
                valueOperator |= symbol != ':';
            }
            previous = token;
            beforePrevious = beforeToken;
        }
        if (groups.Count != 1)
            throw new ArgumentException("The Excel formula delimiters are unbalanced.");
        if (expression is not (Expression.Value or Expression.Reference))
            throw InvalidExpression();
    }

    private static void ValidateArrayConstant(string formula, IEnumerator<Token> tokens)
    {
        var width = 0;
        var columns = 0;
        while (tokens.MoveNext())
        {
            var token = tokens.Current;
            var signed = token.Kind == TokenKind.Symbol && formula[token.Start] is '+' or '-';
            if (signed)
            {
                if (!tokens.MoveNext())
                    throw InvalidExpression();
                token = tokens.Current;
            }

            var constant = token.Kind switch
            {
                TokenKind.Literal => !signed || formula[token.Start] != '"',
                TokenKind.Error => !signed,
                TokenKind.Name => !signed && IsBoolean(formula.AsSpan(token.Start, token.End - token.Start)),
                _ => false
            };
            if (!constant || !tokens.MoveNext() || tokens.Current.Kind != TokenKind.Symbol)
                throw InvalidExpression();

            columns++;
            var delimiter = formula[tokens.Current.Start];
            if (delimiter == ',')
                continue;
            if (delimiter is not (';' or '}') || width != 0 && columns != width)
                throw InvalidExpression();
            if (delimiter == '}')
                return;
            width = columns;
            columns = 0;
        }
        throw InvalidExpression();
    }

    private static ArgumentException InvalidExpression() =>
        new("The Excel formula contains an incomplete or invalid expression.");

    private static bool IsBoolean(ReadOnlySpan<char> token) => token.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("FALSE", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<(string First, string? Last)> LocalSheetReferences(string formula)
    {
        if (!formula.Contains('!'))
            yield break;

        foreach (var token in Tokens(formula))
        {
            if (token.Kind != TokenKind.Sheet)
                continue;
            var qualifier = formula[token.Start..(token.End - 1)];
            if (qualifier.Contains('[') || qualifier.Equals("#REF", StringComparison.OrdinalIgnoreCase))
                continue;
            // Excel can quote the entire 3-D qualifier. A colon cannot be part of a local sheet name.
            var separator = qualifier.IndexOf(':');
            if (separator < 0)
                yield return (Unquote(qualifier), null);
            else
                yield return (Unquote(qualifier[..separator]), Unquote(qualifier[(separator + 1)..]));
        }

        static string Unquote(string name) => name.Trim('\'').Replace("''", "'", StringComparison.Ordinal);
    }

    internal static IEnumerable<(string? Name, string? FirstColumn, string? LastColumn)> LocalTableReferences(string formula)
    {
        if (!formula.Contains('['))
            yield break;

        var previous = new Token(TokenKind.Symbol, 0, 0);
        var beforePrevious = previous;
        foreach (var token in Tokens(formula))
        {
            if (token.Kind == TokenKind.StructuredReference)
            {
                var named = previous.Kind == TokenKind.Name && previous.End == token.Start;
                var qualifier = named ? beforePrevious : previous;
                var external = false;
                if (qualifier.Kind == TokenKind.Sheet)
                {
                    var prefix = formula.AsSpan(qualifier.Start, qualifier.End - qualifier.Start - 1);
                    if (!named || prefix.Length < 3 || prefix[0] != '[' || prefix[^1] != ']' ||
                        !uint.TryParse(prefix[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                        throw new ArgumentException("A structured reference only supports a workbook index as its qualifier.");
                    external = index != 0;
                }

                var (first, last) = ReadColumns(formula.AsSpan(token.Start + 1, token.End - token.Start - 2));
                if (!external)
                    yield return (named ? formula[previous.Start..previous.End] : null, first, last);
            }

            beforePrevious = previous;
            previous = token;
        }

        // MS-XLSX permits a simple column, a row specifier, or nested selectors.
        // Excel also uses @ as shorthand for #This Row.
        static (string? First, string? Last) ReadColumns(ReadOnlySpan<char> inner)
        {
            var thisRow = inner.StartsWith("@", StringComparison.Ordinal);
            if (thisRow)
                inner = inner[1..];
            if (inner.IsEmpty)
                return default;
            if (!inner.TrimStart(' ').StartsWith("[", StringComparison.Ordinal))
            {
                var (column, keyword) = ReadSelector(inner);
                if (thisRow && keyword is not null)
                    throw InvalidSelector();
                return (column, null);
            }

            inner = inner.Trim(' ');
            var (first, row) = ReadBracketedSelector(ref inner);
            if (row is not null)
            {
                if (thisRow)
                    throw InvalidSelector();
                if (inner.IsEmpty)
                    return default;
                ReadComma(ref inner);
                (first, var nextRow) = ReadBracketedSelector(ref inner);
                if (nextRow is not null)
                {
                    if ((row, nextRow) is not (("#HEADERS", "#DATA") or ("#DATA", "#TOTALS")))
                        throw InvalidSelector();
                    if (inner.IsEmpty)
                        return default;
                    ReadComma(ref inner);
                    (first, _) = ReadBracketedSelector(ref inner);
                }
            }

            if (first is null)
                throw InvalidSelector();
            if (inner.IsEmpty)
                return (first, null);
            if (inner[0] != ':')
                throw InvalidSelector();
            inner = inner[1..].TrimStart(' ');
            var (last, _) = ReadBracketedSelector(ref inner);
            if (last is null || !inner.IsEmpty)
                throw InvalidSelector();
            return (first, last);
        }

        static void ReadComma(ref ReadOnlySpan<char> text)
        {
            if (text[0] != ',')
                throw InvalidSelector();
            text = text[1..].TrimStart(' ');
        }

        static (string? Column, string? Keyword) ReadBracketedSelector(ref ReadOnlySpan<char> text)
        {
            if (text.IsEmpty || text[0] != '[')
                throw InvalidSelector();
            for (var i = 1; i < text.Length; i++)
            {
                if (text[i] == '\'')
                    i++;
                else if (text[i] == ']')
                {
                    var selector = ReadSelector(text[1..i].Trim(' '));
                    text = text[(i + 1)..].TrimStart(' ');
                    return selector;
                }
            }
            throw InvalidSelector();
        }

        static (string? Column, string? Keyword) ReadSelector(ReadOnlySpan<char> text)
        {
            if (text.IsEmpty || text[0] == ' ' || text[^1] == ' ')
                throw InvalidSelector();
            if (text[0] == '#')
            {
                var keyword = text.ToString().ToUpperInvariant();
                if (keyword is not ("#ALL" or "#DATA" or "#HEADERS" or "#TOTALS" or "#THIS ROW"))
                    throw InvalidSelector();
                return (null, keyword);
            }

            var column = new System.Text.StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\'')
                {
                    i++;
                    if (i == text.Length || text[i] is not ('\'' or '[' or ']' or '#' or '@'))
                        throw InvalidSelector();
                }
                else if (text[i] is '[' or ']' or '#')
                    throw InvalidSelector();
                column.Append(text[i]);
            }
            return (column.ToString(), null);
        }

        static ArgumentException InvalidSelector() => new("The Excel formula contains an invalid structured reference selector.");
    }

    private static IEnumerable<Token> Tokens(string formula)
    {
        for (var i = 0; i < formula.Length;)
        {
            if (char.IsWhiteSpace(formula[i]))
            {
                i++;
                continue;
            }
            var start = i;
            var match = FormulaToken().Match(formula, i);
            if (match.Success)
            {
                i += match.Length;
                var kind = match.Groups["sheet"].Success ? TokenKind.Sheet :
                    match.Groups["name"].Success ? TokenKind.Name :
                    match.Groups["rowRange"].Success ? TokenKind.RowRange :
                    match.Groups["columnRange"].Success ? TokenKind.ColumnRange :
                    match.Groups["error"].Success ? TokenKind.Error : TokenKind.Literal;
                yield return new Token(kind, start, i);
            }
            else if (formula[i] == '[')
            {
                i = EndOfStructuredReference(formula, i);
                yield return new Token(TokenKind.StructuredReference, start, i);
            }
            else
            {
                var symbol = formula[i++];
                if (i < formula.Length && (symbol == '<' && formula[i] is '=' or '>' ||
                    symbol == '>' && formula[i] == '='))
                    i++;
                yield return new Token(TokenKind.Symbol, start, i);
            }
        }
    }

    private static int EndOfStructuredReference(string formula, int start)
    {
        var depth = 1;
        for (var i = start + 1; i < formula.Length; i++)
        {
            switch (formula[i])
            {
                case '\'':
                    i++;
                    break;
                case '[':
                    depth++;
                    break;
                case ']' when --depth == 0:
                    return i + 1;
            }
        }
        throw new ArgumentException("The Excel formula delimiters are unbalanced.");
    }

    [GeneratedRegex("""
        \G (?:
            (?<sheet>
                (?: '(?:[^']|'')*' | (?:\[[^\[\]]+\])?[\p{L}\p{M}\p{N}_.]+ | \[[^\[\]]+\] )
                (?: : (?: '(?:[^']|'')*' | [\p{L}\p{M}\p{N}_.]+ ) )? !
            )
            | (?<sheet> \#REF! (?=[\p{L}\p{N}_$\\\[]) )
            | "(?:[^"]|"")*"
            | (?<rowRange> (?: \$?[0-9]+\s*:\s*(?:\$?[0-9]+|\#REF!) | \#REF!\s*:\s*\$?[0-9]+ ) )
                (?![\p{L}\p{M}\p{N}_.$])
            | (?<columnRange> (?: \$[A-Z]+\s*:\s*(?:\$?[A-Z]+|\#REF!) | (?:[A-Z]+|\#REF!)\s*:\s*\$[A-Z]+ ) )
                (?![\p{L}\p{M}\p{N}_.$]|\s*[!(])
            | (?<error> \#(?:NULL!|DIV/0!|VALUE!|REF!|NAME\?|NUM!|N/A|GETTING_DATA|SPILL!|CALC!|FIELD!|BLOCKED!|UNKNOWN!|CONNECT!|BUSY!|PYTHON!|TIMEOUT!) )
            | (?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:E[+-]?[0-9]+)?
            | (?<name> \$?[\p{L}_\\][\p{L}\p{M}\p{N}_.]*(?:\$[0-9]+)? )
        )
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FormulaToken();

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

    private const int MaxFunctionArguments = 255;

    // Selected worksheet signatures from MS-XLSX 2.2.2 and Excel's function reference.
    // Match the full stored name so qualified calls can have their own signatures.
    private static readonly Dictionary<string, (int Min, int Max)> FunctionArities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ABS"] = (1, 1),
        ["CHOOSE"] = (2, MaxFunctionArguments),
        ["COLUMN"] = (0, 1),
        ["DATE"] = (3, 3),
        ["FALSE"] = (0, 0),
        ["HLOOKUP"] = (3, 4),
        ["IF"] = (1, 3),
        ["IFERROR"] = (2, 2),
        ["INDEX"] = (2, 4),
        ["INDIRECT"] = (1, 2),
        ["LEFT"] = (1, 2),
        ["LEN"] = (1, 1),
        ["LOG10"] = (1, 1),
        ["MATCH"] = (2, 3),
        ["MID"] = (3, 3),
        ["NA"] = (0, 0),
        ["NOW"] = (0, 0),
        ["OFFSET"] = (3, 5),
        ["PI"] = (0, 0),
        ["RAND"] = (0, 0),
        ["RIGHT"] = (1, 2),
        ["ROUND"] = (2, 2),
        ["ROW"] = (0, 1),
        ["SQRT"] = (1, 1),
        ["SUM"] = (1, MaxFunctionArguments),
        ["SUMIF"] = (2, 3),
        ["TEXT"] = (2, 2),
        ["TODAY"] = (0, 0),
        ["TRUE"] = (0, 0),
        ["VLOOKUP"] = (3, 4),
        ["_xlfn.XLOOKUP"] = (3, 6)
    };

    private static void ValidateFunctionArity(string? name, int argumentCount)
    {
        if (argumentCount > MaxFunctionArguments)
            throw new ArgumentException($"An Excel function call cannot have more than {MaxFunctionArguments} arguments.");
        if (name is not null && FunctionArities.TryGetValue(name, out var arity) &&
            (argumentCount < arity.Min || argumentCount > arity.Max))
            throw new ArgumentException($"Function '{name}' has an invalid number of arguments.");
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

    // On the moving axis, each intact endpoint must stay in place or move with the formula.
    // A broken endpoint provides no anchor or coordinate evidence.
    internal static bool CompatibleReferences(string left, string right, int displacement = 0)
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
                    if (left[anchor] == '?' || right[anchor] == '?')
                        continue;
                    var from = left.AsSpan(coordinate, CoordinateLength);
                    var to = right.AsSpan(coordinate, CoordinateLength);
                    if (from.SequenceEqual(to))
                        continue;
                    if (displacement == 0 || int.Parse(to, CultureInfo.InvariantCulture) -
                        int.Parse(from, CultureInfo.InvariantCulture) != displacement)
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

    private static bool TryCellAxes(ReadOnlySpan<char> text, out int separator, out int rowStart, out CellAddress address)
    {
        var columnStart = text[0] == '$' ? 1 : 0;
        separator = columnStart;
        while (separator < text.Length && char.IsAsciiLetter(text[separator]))
            separator++;
        rowStart = separator < text.Length && text[separator] == '$' ? separator + 1 : separator;
        var column = AxisNumber(text[columnStart..separator], row: false);
        var row = AxisNumber(text[separator..], row: true);
        address = new CellAddress(row, column);
        return column is >= 1 and <= CellAddress.LastColumn && row is >= 1 and <= CellAddress.LastRow;
    }

    private static string RewriteRange(Match match, char kind, int rowOffset, int columnOffset,
        ReferenceRewrite rewrite, bool? fixedRow)
    {
        var colon = match.Groups["colon"];
        var separator = colon.Index - match.Index;
        var range = match.Value;
        if (kind != 'B' && !IsGridRange(range, kind == 'R'))
            return range;

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
        if (endpoint[0] == '#')
            return ('?', 0);
        if (kind != 'B')
        {
            var row = kind == 'R';
            var coordinate = AxisNumber(endpoint, row);
            return (endpoint[0] == '$' ? '$' : '-', fixedRow == row ? coordinate : 0);
        }
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
        if (axis[0] == '$')
            return axis;
        var number = AxisNumber(axis, row) + offset;
        var limit = row ? CellAddress.LastRow : CellAddress.LastColumn;
        // A NUL cannot occur in workbook XML, so it separates reference markers from literal text.
        if (rewrite == ReferenceRewrite.Relative)
            return string.Concat(row ? "\0R" : "\0C", number.ToString(CultureInfo.InvariantCulture), "\0");
        if (number < 1 || number > limit)
            return null;
        return row ? number.ToString(CultureInfo.InvariantCulture) : new CellAddress(1, number).ToString()[..^1];
    }

    private static bool IsGridRange(ReadOnlySpan<char> range, bool row)
    {
        var separator = range.IndexOf(':');
        return IsGridEndpoint(range[..separator].TrimEnd(), row) &&
            IsGridEndpoint(range[(separator + 1)..].TrimStart(), row);
    }

    private static bool IsGridEndpoint(ReadOnlySpan<char> endpoint, bool row)
    {
        if (endpoint[0] == '#')
            return true;
        var coordinate = AxisNumber(endpoint, row);
        return coordinate >= 1 && coordinate <= (row ? CellAddress.LastRow : CellAddress.LastColumn);
    }

    private static int AxisNumber(ReadOnlySpan<char> axis, bool row)
    {
        if (!axis.IsEmpty && axis[0] == '$')
            axis = axis[1..];
        if (!row)
            return axis.Length <= 3 ? ColumnNumber(axis) : 0;
        return int.TryParse(axis, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static int ColumnNumber(ReadOnlySpan<char> axis)
    {
        var column = 0;
        foreach (var letter in axis)
        {
            if (!char.IsAsciiLetter(letter))
                return 0;
            column = column * 26 + char.ToUpperInvariant(letter) - 'A' + 1;
        }
        return column;
    }

    // A colon can join sheet names before !, or join references before a function call.
    [GeneratedRegex("""
        (?<![\p{L}\p{M}\p{N}_.$\\]) (?: '(?:[^']|'')*' | (?:\[[^\[\]]+\])?[\p{L}\p{M}\p{N}_.]+ | \[[^\[\]]+\] )
            (?: \s*:\s* (?: '(?:[^']|'')*' | [\p{L}\p{M}\p{N}_.]+ ) )? \s* !
        | "(?:[^"]|"")*" | '(?:[^']|'')*' | \[(?:'['\[\]#@]|[^\[\]]|\[(?:'['\[\]#@]|[^\[\]])*\])*\]
        | (?<![\p{L}\p{M}\p{N}_.$\\]) [\p{L}_][\p{L}\p{M}\p{N}_.]*\s*(?=\()
        | (?<![\p{L}\p{M}\p{N}_.$\\]) (?:
            (?:\$?[A-Z]{1,3}\$?[1-9][0-9]{0,6}|\#REF!) (?<colon>\s*:\s*) (?:\$?[A-Z]{1,3}\$?[1-9][0-9]{0,6}|\#REF!)
                (?!\s*[!(])
            | (?<reference>\$?[A-Z]{1,3}\$?[1-9][0-9]{0,6})
            | (?<rows>\$?[0-9]+(?<colon>\s*:\s*)(?:\$?[0-9]+|\#REF!)|\#REF!(?<colon>\s*:\s*)\$?[0-9]+)
            | (?<columns>\$?[A-Z]{1,3}(?<colon>\s*:\s*)(?:\$?[A-Z]{1,3}|\#REF!)|\#REF!(?<colon>\s*:\s*)\$?[A-Z]{1,3})(?!\s*[!(])
            | (?<broken>\#REF!)
        ) (?![\p{L}\p{M}\p{N}_.$\\])
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceToken();
}
