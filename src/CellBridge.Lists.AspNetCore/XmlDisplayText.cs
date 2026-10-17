using System.Globalization;
using System.Text;

namespace CellBridge.Lists.AspNetCore;

internal static class XmlDisplayText
{
    // Escape XML-invalid UTF-16 units and literal escape sequences without
    // changing canonical schema names or record text. SpreadsheetML readers
    // decode this form; OData metadata consumers see its literal display text.
    internal static string Encode(string text)
    {
        var output = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '_' && i + 6 < text.Length && text[i + 1] == 'x' && text[i + 6] == '_' &&
                text.AsSpan(i + 2, 4).ToString().All(Uri.IsHexDigit)) output.Append("_x005F_");
            else if (c is '\t' or '\n' or '\r' || c >= 0x20 && c is not '\ufffe' and not '\uffff' && !char.IsSurrogate(c)) output.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { output.Append(c); output.Append(text[++i]); }
            else output.Append("_x").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
        }
        return output.ToString();
    }
}
