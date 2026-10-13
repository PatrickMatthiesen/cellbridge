using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CellBridge.Lists;
using Microsoft.AspNetCore.Http;

namespace CellBridge.Lists.AspNetCore;

internal static class ListExtract
{
    internal static IReadOnlyList<ListField> Projection(ListDefinition definition, string? fields)
    {
        if (fields is null) return definition.Fields.Where(f => f.Display?.Hidden != true).OrderBy(f => f.Display?.Order ?? 0).ToArray();
        var ids = fields.Split(',').Select(x => Guid.TryParse(x, out var id) ? id : throw HttpState.Invalid()).ToArray();
        if (ids.Length > definition.Fields.Count || ids.Distinct().Count() != ids.Length) throw HttpState.Invalid();
        return ids.Select(id => definition.Fields.SingleOrDefault(f => f.Id == id) ?? throw HttpState.Invalid()).ToArray();
    }

    internal static async Task<IResult> Export(HttpState s, bool xlsx)
    {
        s.CheckQuery("fields", "limit");
        var definition = await s.Definition();
        var fields = Projection(definition, s.SingleQuery("fields"));
        var limitText = s.SingleQuery("limit");
        var limit = limitText is null ? s.Options.Limits.MaxPageSize : int.TryParse(limitText, out var n) ? n : throw HttpState.Invalid();
        var query = s.Query(new ListQuery(Limit: limit), null, definition);
        var page = await s.Engine.QueryAsync(s.Actor, s.Workspace, s.List, query, s.Ct);
        s.CheckSchema(definition, page);
        s.Context.Response.Headers["X-CellBridge-Export-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
        s.Context.Response.Headers["X-CellBridge-Export-Has-More"] = page.NextRecordId is null ? "false" : "true";
        s.Context.Response.Headers["X-CellBridge-Schema-Version"] = page.SchemaVersion.ToString(CultureInfo.InvariantCulture);
        return xlsx ? Results.File(Xlsx(fields, page.Records), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "records.xlsx")
            : Results.File(Encoding.UTF8.GetBytes(Csv(fields, page.Records)), "text/csv; charset=utf-8", "records.csv");
    }

    internal static string Csv(IReadOnlyList<ListField> fields, IReadOnlyList<ListRecord> records)
    {
        var output = new StringBuilder();
        output.AppendLine(string.Join(',', new[] { "id", "number" }.Concat(fields.Select(f => Quote(f.Name)))));
        foreach (var record in records)
            output.Append(record.Id).Append(',').Append(record.Number.ToString(CultureInfo.InvariantCulture))
                .Append(fields.Count == 0 ? "" : "," + string.Join(',', fields.Select(f => Quote(Text(record.Values[f.Id.ToString()]), f.Type is not ListFieldType.Integer and not ListFieldType.Decimal and not ListFieldType.Boolean)))).Append("\r\n");
        return output.ToString();
    }

    private static string Quote(string text, bool protectFormula = true)
    {
        // Quoting alone does not prevent spreadsheet formula execution. Prefix text
        // beginning with a formula trigger, including triggers after whitespace.
        var trimmed = text.TrimStart();
        if (protectFormula && (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) || text.StartsWith('\t') || text.StartsWith('\r') || text.StartsWith('\n'))) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    internal static string Text(JsonNode? value) => value is null ? "" : value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value.ToJsonString();

    internal static byte[] Xlsx(IReadOnlyList<ListField> fields, IReadOnlyList<ListRecord> records)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var data = new XElement(ns + "sheetData");
        data.Add(new XElement(ns + "row", new[] { "id", "number" }.Concat(fields.Select(f => f.Name)).Select(Inline)));
        foreach (var record in records)
        {
            var number = record.Number.ToString(CultureInfo.InvariantCulture);
            var row = new XElement(ns + "row", Inline(record.Id.ToString()), NeedsTextNumber(number) ? Inline(number) : Numeric(number));
            foreach (var field in fields)
            {
                var node = record.Values[field.Id.ToString()];
                var text = Text(node);
                if (node is null) row.Add(Inline(""));
                else if (field.Type is ListFieldType.Integer or ListFieldType.Decimal)
                    row.Add(NeedsTextNumber(text) ? Inline(text) : Numeric(text));
                else if (field.Type == ListFieldType.Boolean) row.Add(new XElement(ns + "c", new XAttribute("t", "b"), new XElement(ns + "v", text == "true" ? "1" : "0")));
                else if (field.Type is ListFieldType.Date or ListFieldType.Timestamp)
                {
                    var date = field.Type == ListFieldType.Date ? DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).UtcDateTime;
                    if (date.Year < 1900) row.Add(Inline(text));
                    else
                    {
                        var serial = date.ToOADate();
                        // Excel's 1900 date system includes a nonexistent leap day.
                        if (date < new DateTime(1900, 3, 1)) serial--;
                        row.Add(new XElement(ns + "c", new XAttribute("s", field.Type == ListFieldType.Date ? "1" : "2"), new XElement(ns + "v", serial.ToString("R", CultureInfo.InvariantCulture))));
                    }
                }
                else row.Add(Inline(text));
            }
            data.Add(row);
        }
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Add("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Records" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add("xl/styles.xml", """<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="3"><xf/><xf numFmtId="14" applyNumberFormat="1"/><xf numFmtId="22" applyNumberFormat="1"/></cellXfs></styleSheet>""");
            Add("xl/worksheets/sheet1.xml", new XDocument(new XElement(ns + "worksheet", data)).ToString());
            void Add(string path, string content) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(content); }
        }
        return stream.ToArray();

        XElement Inline(string text)
        {
            // Excel's cell limit applies to the original text. SpreadsheetML
            // escapes can expand its XML representation without adding characters.
            // Count UTF-16 units conservatively so supplementary characters cannot
            // exceed the limit on readers which count surrogate pairs separately.
            if (text.Length > 32767)
                throw new ListException(ListFailure.Limit, "XLSX cell text exceeds 32767 UTF-16 units.");
            return new XElement(ns + "c", new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), SafeXml(text))));
        }
        XElement Numeric(string text) => new(ns + "c", new XElement(ns + "v", text));
    }

    // Excel stores at most 15 significant numeric digits. Text cells preserve
    // large identities and decimal precision without rounding.
    private static bool NeedsTextNumber(string text)
    {
        var number = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        var digits = number.ToString("G29", CultureInfo.InvariantCulture).Replace("-", "").Replace(".", "").TrimStart('0');
        return digits.Length > 15;
    }

    // SpreadsheetML's escaped character form preserves controls that XML cannot
    // carry. Escape literal escape sequences first so Excel does not alter text.
    private static string SafeXml(string text)
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
