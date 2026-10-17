using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CellBridge.Lists;
using Microsoft.AspNetCore.Http;

namespace CellBridge.Lists.AspNetCore;

internal static class ODataFeed
{
    private static readonly Regex Predicate = new(@"\G\s*(f_[0-9a-f]{32})\s+(eq|ne|gt|ge|lt|le)\s+('(?:[^']|'')*'|null|true|false|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)?|-?\d+(?:\.\d+)?)\s*(and\s+|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static string FieldName(ListField field) => "f_" + field.Id.ToString("N");
    private static void Enabled(HttpState s)
    {
        if (!s.Options.EnableOData) throw new ListException(ListFailure.NotFound, "Not found.");
        s.Context.Response.Headers["OData-Version"] = "4.0";
    }

    internal static async Task<IResult> Service(HttpState s)
    {
        Enabled(s); s.CheckQuery(); await s.Definition();
        return Results.Json(new Dictionary<string, object?> { ["@odata.context"] = Base(s) + "/$metadata", ["value"] = new[] { new { name = "Records", kind = "EntitySet", url = "Records" } } });
    }

    internal static async Task<IResult> Metadata(HttpState s)
    {
        Enabled(s); s.CheckQuery(); var definition = await s.Definition();
        XNamespace edm = "http://docs.oasis-open.org/odata/ns/edm";
        XNamespace edmx = "http://docs.oasis-open.org/odata/ns/edmx";
        var entity = new XElement(edm + "EntityType", new XAttribute("Name", "Record"),
            new XElement(edm + "Key", new XElement(edm + "PropertyRef", new XAttribute("Name", "Id"))),
            Property("Id", "Edm.Guid", false), Property("Number", "Edm.Int64", false), Property("Revision", "Edm.Int64", false));
        foreach (var field in definition.Fields)
        {
            var property = Property(FieldName(field), EdmType(field.Type), !field.Required);
            if (field.Type == ListFieldType.Text) property.Add(new XAttribute("MaxLength", field.MaxLength));
            if (field.Type == ListFieldType.Decimal)
            {
                // A fixed Scale=12 would limit integral values to 16 digits.
                // CSDL has no independent maximum-scale facet, so describe the
                // decimal domain and declare the engine's extra constraint.
                property.Add(new XAttribute("Precision", ListValidation.DecimalPrecision));
                property.Add(new XAttribute("Scale", "variable"));
                property.Add(new XElement(edm + "Annotation", new XAttribute("Term", "CellBridge.Lists.MaximumDecimalScale"), new XAttribute("Int", ListValidation.DecimalScale)));
            }
            if (field.Type == ListFieldType.Timestamp) property.Add(new XAttribute("Precision", "7"));
            property.Add(new XElement(edm + "Annotation", new XAttribute("Term", "Org.OData.Core.V1.Description"), new XAttribute("String", XmlDisplayText.Encode(field.Name))));
            entity.Add(property);
        }
        var document = new XDocument(new XElement(edmx + "Edmx", new XAttribute("Version", "4.0"), new XElement(edmx + "DataServices",
            new XElement(edm + "Schema", new XAttribute("Namespace", "CellBridge.Lists"),
                new XElement(edm + "Term", new XAttribute("Name", "MaximumDecimalScale"), new XAttribute("Type", "Edm.Int32"), new XAttribute("AppliesTo", "Property")), entity,
                new XElement(edm + "EntityContainer", new XAttribute("Name", "List"), new XElement(edm + "EntitySet", new XAttribute("Name", "Records"), new XAttribute("EntityType", "CellBridge.Lists.Record")))))));
        return Results.Text(document.ToString(), "application/xml; charset=utf-8");
        XElement Property(string name, string type, bool nullable) => new(edm + "Property", new XAttribute("Name", name), new XAttribute("Type", type), new XAttribute("Nullable", nullable ? "true" : "false"));
    }

    internal static async Task<IResult> Records(HttpState s)
    {
        Enabled(s); s.CheckQuery("$top", "$select", "$filter", "$orderby", "$skiptoken");
        var definition = await s.Definition();
        var topText = s.SingleQuery("$top");
        var top = topText is null ? Math.Min(100, s.Options.Limits.MaxPageSize) : int.TryParse(topText, out var n) ? n : throw HttpState.Invalid();
        if (top < 0 || top > s.Options.Limits.MaxPageSize) throw HttpState.Invalid();
        var selected = s.SingleQuery("$select");
        var fieldMap = definition.Fields.ToDictionary(FieldName);
        IReadOnlyList<ListField> fields;
        HashSet<string>? selectedNames = null;
        if (selected is null) fields = ListExtract.Projection(definition, null);
        else
        {
            var names = selected.Split(',').Select(x => x.Trim()).ToArray();
            if (names.Length == 0 || names.Distinct().Count() != names.Length) throw HttpState.Invalid();
            selectedNames = names.ToHashSet(StringComparer.Ordinal);
            fields = names.Where(x => x is not "Id" and not "Number" and not "Revision")
                .Select(x => fieldMap.TryGetValue(x, out var f) ? f : throw HttpState.Invalid()).ToArray();
        }
        var filters = ParseFilter(s.SingleQuery("$filter"), fieldMap, s.Options.Limits.MaxFilters);
        Guid? sort = null; var descending = false;
        if (s.SingleQuery("$orderby") is { } order)
        {
            var parts = order.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 2 || !fieldMap.TryGetValue(parts[0], out var field) || parts.Length == 2 && parts[1] is not "asc" and not "desc") throw HttpState.Invalid();
            sort = field.Id; descending = parts.Length == 2 && parts[1] == "desc";
        }
        var query = new ListQuery(filters, sort, descending, Limit: Math.Max(1, top));
        var purpose = "odata:" + selected;
        var applied = s.Query(query, s.SingleQuery("$skiptoken"), definition, purpose);
        var page = top == 0 ? new RecordPage([], null, definition.SchemaVersion)
            : await s.Engine.QueryAsync(s.Actor, s.Workspace, s.List, applied, s.Ct);
        s.CheckSchema(definition, page);
        var ieee754 = Ieee754Compatible(s);
        var rows = page.Records.Select(record =>
        {
            var row = new JsonObject();
            if (selectedNames is null || selectedNames.Contains("Id")) row["Id"] = record.Id.ToString();
            if (selectedNames is null || selectedNames.Contains("Number")) row["Number"] = ieee754 ? JsonValue.Create(record.Number.ToString(CultureInfo.InvariantCulture)) : JsonValue.Create(record.Number);
            if (selectedNames is null || selectedNames.Contains("Revision")) row["Revision"] = ieee754 ? JsonValue.Create(record.Revision.ToString(CultureInfo.InvariantCulture)) : JsonValue.Create(record.Revision);
            foreach (var field in fields)
            {
                var value = record.Values[field.Id.ToString()];
                row[FieldName(field)] = ieee754 && value is not null && field.Type is ListFieldType.Integer or ListFieldType.Decimal
                    ? JsonValue.Create(ListExtract.Text(value)) : value?.DeepClone();
            }
            return row;
        }).ToArray();
        var response = new Dictionary<string, object?> { ["@odata.context"] = Base(s) + "/$metadata#Records" + (selectedNames is null ? "" : "(" + string.Join(',', selectedNames) + ")"), ["value"] = rows };
        // $top limits the entire result, not each server page. Explicit bounded
        // requests fit one query; only requests without $top use server paging.
        if (topText is null && s.Cursor(query, definition, page.NextRecordId, purpose) is { } token)
        {
            var args = s.Context.Request.Query.Where(x => x.Key != "$skiptoken").ToDictionary(x => x.Key, x => (string?)x.Value.ToString());
            args["$skiptoken"] = token;
            response["@odata.nextLink"] = Base(s) + "/Records" + QueryString.Create(args);
        }
        return Results.Json(response, contentType: "application/json;IEEE754Compatible=" + (ieee754 ? "true" : "false"));
    }

    private static bool Ieee754Compatible(HttpState s)
    {
        try
        {
            var accepted = s.Context.Request.GetTypedHeaders().Accept;
            var json = accepted?.Where(media => media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                    || media.MediaType.Equals("application/*", StringComparison.OrdinalIgnoreCase) || media.MediaType.Equals("*/*", StringComparison.OrdinalIgnoreCase))
                .Where(media => (media.Quality ?? 1) > 0).OrderByDescending(media => media.Quality ?? 1).FirstOrDefault();
            var parameter = json?.Parameters.FirstOrDefault(p => p.Name.Equals("IEEE754Compatible", StringComparison.OrdinalIgnoreCase));
            if (parameter is null) return false;
            var value = parameter.Value.ToString().Trim('"');
            if (!bool.TryParse(value, out var enabled)) throw HttpState.Invalid();
            return enabled;
        }
        catch (FormatException) { throw HttpState.Invalid(); }
    }

    private static IReadOnlyList<RecordFilter>? ParseFilter(string? expression, IReadOnlyDictionary<string, ListField> fields, int limit)
    {
        if (expression is null) return null;
        if (expression.Length == 0) throw HttpState.Invalid();
        var filters = new List<RecordFilter>(); var offset = 0;
        while (offset < expression.Length)
        {
            var match = Predicate.Match(expression, offset);
            if (!match.Success || match.Index != offset || !fields.TryGetValue(match.Groups[1].Value, out var field)) throw HttpState.Invalid();
            var literal = match.Groups[3].Value;
            JsonNode? value;
            if (literal == "null") value = null;
            else if (literal.StartsWith('\'')) value = JsonValue.Create(literal[1..^1].Replace("''", "'"));
            else if (field.Type == ListFieldType.Reference)
            {
                if (!Guid.TryParseExact(literal, "D", out var reference)) throw HttpState.Invalid();
                value = JsonValue.Create(reference.ToString("D"));
            }
            else if (field.Type is ListFieldType.Date or ListFieldType.Timestamp) value = JsonValue.Create(literal);
            else { try { value = JsonNode.Parse(literal); } catch { throw HttpState.Invalid(); } }
            var op = match.Groups[2].Value switch { "eq" => FilterOperator.Equal, "ne" => FilterOperator.NotEqual, "gt" => FilterOperator.GreaterThan,
                "ge" => FilterOperator.GreaterOrEqual, "lt" => FilterOperator.LessThan, _ => FilterOperator.LessOrEqual };
            filters.Add(new RecordFilter(field.Id, op, value));
            if (filters.Count > limit) throw new ListException(ListFailure.Limit, "Filter limit exceeded.");
            offset += match.Length;
            if (offset == expression.Length && match.Groups[4].Value.Length != 0) throw HttpState.Invalid();
        }
        return filters;
    }

    private static string Base(HttpState s)
    {
        var path = s.Context.Request.Path.Value!;
        var marker = path.LastIndexOf("/odata", StringComparison.OrdinalIgnoreCase);
        // Relative links avoid trusting a request-controlled Host header.
        return s.Context.Request.PathBase + path[..(marker + 6)];
    }
    private static string EdmType(ListFieldType type) => type switch
    {
        ListFieldType.Boolean => "Edm.Boolean", ListFieldType.Integer => "Edm.Int64", ListFieldType.Decimal => "Edm.Decimal",
        ListFieldType.Date => "Edm.Date", ListFieldType.Timestamp => "Edm.DateTimeOffset", ListFieldType.Reference => "Edm.Guid", _ => "Edm.String"
    };
}
