using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CellBridge.Lists;

/// <summary>Validation shared by all stores. Display metadata never controls write authorization.</summary>
public static class ListValidation
{
    public const int DecimalPrecision = 28;
    public const int DecimalScale = 12;

    public static void ValidateDefinition(ListDefinition definition, ListLimits limits)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(limits);
        Require(definition.Id != Guid.Empty && definition.WorkspaceId != Guid.Empty, "List and workspace IDs must be nonempty.");
        Require(definition.SchemaVersion > 0, "Schema version must be positive.");
        Text(definition.Name, 200, "List name");
        Require(definition.TemplateId.HasValue == definition.TemplateVersion.HasValue, "Template ID and version must be supplied together.");
        if (definition.TemplateId.HasValue)
            Require(definition.TemplateId != Guid.Empty && definition.TemplateVersion > 0, "Template identity is invalid.");
        if (definition.NumberPrefix is not null) Text(definition.NumberPrefix, 32, "Number prefix");
        Require(definition.Fields is not null, "Fields are required.");
        Budget(definition.Fields!.Count <= limits.MaxFields, "Field count exceeds the configured limit.");
        Require(definition.Fields.All(f => f is not null), "Field definitions cannot be null.");
        Require(definition.Fields.Select(f => f.Id).Distinct().Count() == definition.Fields.Count, "Duplicate field IDs.");
        Require(definition.Fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() == definition.Fields.Count, "Duplicate field names.");
        foreach (var field in definition.Fields)
        {
            Require(field.Id != Guid.Empty && Enum.IsDefined(field.Type), "Field identity or type is invalid.");
            Text(field.Name, 200, "Field name");
            Require(field.MaxLength is > 0 and <= 65536, "Field maximum length must be between 1 and 65536.");
            if (field.Type == ListFieldType.Choice)
            {
                Require(field.Choices is { Count: > 0 and <= 256 }, "A choice field needs 1 to 256 choices.");
                Require(field.Choices!.All(c => c is not null), "Choices cannot be null.");
                Require(field.Choices!.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == field.Choices.Count, "Duplicate choice IDs.");
                foreach (var choice in field.Choices!) { Text(choice.Id, 64, "Choice ID"); Text(choice.Label, 200, "Choice label"); }
            }
            else Require(field.Choices is null or { Count: 0 }, "Only choice fields may declare choices.");
            if (field.Type == ListFieldType.Reference)
                Require(field.ReferenceListId is not null && field.ReferenceListId != Guid.Empty && field.ReferenceDisplayFieldId != Guid.Empty, "A reference field needs a nonempty target list ID.");
            else Require(field.ReferenceListId is null && field.ReferenceDisplayFieldId is null, "Only reference fields may declare a target.");
            if (field.Display is { } display)
            {
                if (display.HelpText is not null) Text(display.HelpText, 2048, "Help text", allowEmpty: true, allowNewlines: true);
                Require(display.Order is >= -1000000 and <= 1000000, "Display order is out of bounds.");
                Require(display.Tags is null or { Count: <= 16 }, "Too many display tags.");
                if (display.Tags is { } tags)
                {
                    foreach (var tag in tags) Text(tag, 64, "Display tag");
                    Require(tags.Distinct(StringComparer.Ordinal).Count() == tags.Count, "Duplicate display tags.");
                }
            }
        }
    }

    public static void ValidateEvolution(ListDefinition previous, ListDefinition next, ListLimits limits)
    {
        ValidateDefinition(previous, limits);
        ValidateDefinition(next, limits);
        Require(previous.Id == next.Id && previous.WorkspaceId == next.WorkspaceId &&
            previous.TemplateId == next.TemplateId && previous.TemplateVersion == next.TemplateVersion &&
            previous.NumberPrefix == next.NumberPrefix, "List ownership and provenance cannot change.");
        Require(previous.SchemaVersion < long.MaxValue && next.SchemaVersion == previous.SchemaVersion + 1, "Schema versions must advance by one.");
        var fields = next.Fields.ToDictionary(f => f.Id);
        foreach (var old in previous.Fields)
        {
            Require(fields.TryGetValue(old.Id, out var current), "Published fields cannot be removed.");
            Require(current!.Protected == old.Protected, "Field provenance cannot change.");
            Require(current.Type == old.Type && current.Required == old.Required && current.MaxLength == old.MaxLength &&
                current.ReferenceListId == old.ReferenceListId && current.ReferenceDisplayFieldId == old.ReferenceDisplayFieldId &&
                ChoiceIds(old).SequenceEqual(ChoiceIds(current), StringComparer.Ordinal), "Published field constraints cannot change.");
            if (old.Protected) Require(SameField(old, current), "Protected template fields cannot change.");
        }
        var oldIds = previous.Fields.Select(f => f.Id).ToHashSet();
        Require(next.Fields.Where(f => !oldIds.Contains(f.Id)).All(f => !f.Required && !f.Protected), "New fields must be optional workspace extensions.");
    }

    public static void ValidateValues(ListDefinition definition, JsonObject values, ListLimits limits)
    {
        ArgumentNullException.ThrowIfNull(values);
        Budget(Encoding.UTF8.GetByteCount(values.ToJsonString()) <= limits.MaxRecordBytes, "Record exceeds the configured byte limit.");
        var fields = definition.Fields.ToDictionary(f => f.Id);
        foreach (var value in values)
        {
            Require(Guid.TryParseExact(value.Key, "D", out var id) && value.Key == id.ToString("D"), "Value keys must be canonical field UUIDs.");
            Require(fields.TryGetValue(id, out var field), "Unknown field ID.");
            ValidateValue(field!, value.Value);
        }
        foreach (var field in definition.Fields.Where(f => f.Required))
            Require(values.TryGetPropertyValue(field.Id.ToString("D"), out var value) && value is not null, "A required field is missing or null.");
    }

    public static JsonObject ApplyChanges(ListDefinition definition, JsonObject values, IReadOnlyList<FieldChange> changes, ListLimits limits)
    {
        ValidateChanges(definition, changes, limits);
        var result = (JsonObject)values.DeepClone();
        foreach (var change in changes)
        {
            var key = change.FieldId.ToString("D");
            if (change.Remove) result.Remove(key);
            else result[key] = change.Value?.DeepClone();
        }
        ValidateValues(definition, result, limits);
        return result;
    }

    public static void ValidateQuery(ListDefinition definition, ListQuery query, ListLimits limits)
    {
        ArgumentNullException.ThrowIfNull(query);
        Budget(query.Limit > 0 && query.Limit <= limits.MaxPageSize, "Page size exceeds the configured bounds.");
        Budget((query.Filters?.Count ?? 0) <= limits.MaxFilters, "Filter count exceeds the configured limit.");
        var fields = definition.Fields.ToDictionary(f => f.Id);
        Require(query.AfterRecordId != Guid.Empty, "Page cursor must be a nonempty record ID.");
        if (query.SortFieldId is { } sort) Require(fields.ContainsKey(sort), "Unknown sort field.");
        foreach (var filter in query.Filters ?? [])
        {
            Require(filter is not null, "Filters cannot be null.");
            Require(fields.TryGetValue(filter.FieldId, out var field), "Unknown filter field.");
            Require(Enum.IsDefined(filter.Operator), "Unknown filter operator.");
            if (filter.Value is null)
                Require(filter.Operator is FilterOperator.Equal or FilterOperator.NotEqual, "Null only supports equality comparisons.");
            else
            {
                ValidateValue(field! with { Required = false }, filter.Value);
                if (field!.Type is ListFieldType.Boolean or ListFieldType.Choice or ListFieldType.Reference)
                    Require(filter.Operator is FilterOperator.Equal or FilterOperator.NotEqual, "This field type only supports equality comparisons.");
            }
        }
    }

    /// <summary>Hashes ID, original schema version, sorted field keys, and canonical JSON scalars. Missing keys differ from null.</summary>
    public static string CreateDigest(CreateRecordRequest request)
    {
        Require(request.Id != Guid.Empty && request.SchemaVersion > 0 && request.Values is not null, "Invalid create identity or schema version.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", request.Id.ToString("D"));
            writer.WriteNumber("schemaVersion", request.SchemaVersion);
            writer.WritePropertyName("values");
            WriteCanonical(writer, request.Values!);
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    internal static void ValidateChanges(ListDefinition definition, IReadOnlyList<FieldChange> changes, ListLimits limits)
    {
        Require(changes is not null, "Changes are required.");
        Budget(changes!.Count <= limits.MaxFields, "Too many field changes.");
        Require(changes.All(c => c is not null), "Changes cannot be null.");
        Require(changes.Select(c => c.FieldId).Distinct().Count() == changes.Count, "Duplicate changed field IDs.");
        var fields = definition.Fields.ToDictionary(f => f.Id);
        foreach (var change in changes)
        {
            Require(fields.TryGetValue(change.FieldId, out var field), "Unknown changed field.");
            Require(!change.Remove || change.Value is null, "Removal cannot also contain a value.");
            if (change.Remove) Require(!field!.Required, "Required fields cannot be removed.");
            else ValidateValue(field!, change.Value);
        }
    }

    internal static object? Scalar(ListField field, JsonNode? value)
    {
        if (value is null) return null;
        using var document = JsonDocument.Parse(value.ToJsonString());
        var json = document.RootElement;
        return field.Type switch
        {
            ListFieldType.Integer => json.GetInt64(),
            ListFieldType.Decimal => json.GetDecimal(),
            ListFieldType.Boolean => json.GetBoolean(),
            _ => json.GetString()
        };
    }

    internal static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new ListException(ListFailure.Validation, message);
    }
    internal static void Budget([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new ListException(ListFailure.Limit, message);
    }
    internal static void Text(string? value, int maximum, string label, bool allowEmpty = false, bool allowNewlines = false)
    {
        Require(value is not null && (allowEmpty || !string.IsNullOrWhiteSpace(value)) && value.Length <= maximum &&
            IsWellFormedText(value) && !value.Any(c => char.IsControl(c) && !(allowNewlines && c is '\r' or '\n' or '\t')), $"{label} is invalid or too long.");
    }

    /// <summary>PostgreSQL text/JSONB cannot represent NUL; unpaired UTF-16 surrogates cannot round-trip through JSON.</summary>
    public static bool IsWellFormedText(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\0' || char.IsLowSurrogate(value[i])) return false;
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
        }
        return true;
    }

    private static IEnumerable<string> ChoiceIds(ListField field) => (field.Choices ?? []).Select(c => c.Id).Order(StringComparer.Ordinal);
    private static bool SameField(ListField left, ListField right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static void ValidateValue(ListField field, JsonNode? value)
    {
        if (value is null) { Require(!field.Required, "A required field cannot be null."); return; }
        try
        {
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var originalText))
                Require(IsWellFormedText(originalText), "Text contains a character that cannot be stored losslessly.");
        }
        catch (InvalidOperationException) { throw new ListException(ListFailure.Validation, "Text encoding is invalid."); }
        catch (ArgumentException) { throw new ListException(ListFailure.Validation, "Text encoding is invalid."); }
        using var document = JsonDocument.Parse(value.ToJsonString());
        var json = document.RootElement;
        var text = json.ValueKind == JsonValueKind.String ? json.GetString()! : null;
        var valid = field.Type switch
        {
            ListFieldType.Text => text is not null && text.Length <= field.MaxLength && IsWellFormedText(text),
            ListFieldType.Boolean => json.ValueKind is JsonValueKind.True or JsonValueKind.False,
            ListFieldType.Integer => json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out _),
            ListFieldType.Decimal => json.ValueKind == JsonValueKind.Number && ValidDecimal(json.GetRawText()),
            ListFieldType.Date => text is not null && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            ListFieldType.Timestamp => text is not null && DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) && text == timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
            ListFieldType.Choice => text is not null && field.Choices?.Any(c => c.Id == text) == true,
            ListFieldType.Reference => text is not null && Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && text == id.ToString("D"),
            _ => false
        };
        Require(valid, "Field value does not match its declared type or bounds.");
    }

    private static bool ValidDecimal(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
        // Parsing can round excess digits. Compare the input coefficient and exponent to the exact decimal result.
        var canonical = number.ToString("G29", CultureInfo.InvariantCulture);
        var parts = value.ToLowerInvariant().Split('e');
        if (parts.Length > 2 || (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))) return false;
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var coefficient = parts[0].TrimStart('-');
        var fraction = coefficient.Contains('.') ? coefficient.Length - coefficient.IndexOf('.') - 1 : 0;
        var digits = coefficient.Replace(".", "").TrimStart('0');
        // Zero has no significant fractional scale. Keep syntactic/exponent parsing and decimal
        // range checks above, but accept equivalent zero spellings regardless of trailing zeros.
        if (digits.Length == 0) return true;
        var trailingZeros = digits.Length - digits.TrimEnd('0').Length;
        var effectiveScale = (long)fraction - exponent - trailingZeros;
        var precision = digits.Length - trailingZeros + Math.Max(0L, -effectiveScale);
        return precision <= DecimalPrecision && effectiveScale <= DecimalScale && canonical.Length > 0;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonObject values)
    {
        writer.WriteStartObject();
        foreach (var property in values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Require(Guid.TryParseExact(property.Key, "D", out var id) && property.Key == id.ToString("D"), "Value keys must be canonical field UUIDs.");
            writer.WritePropertyName(property.Key);
            if (property.Value is null) { writer.WriteNullValue(); continue; }
            using var document = JsonDocument.Parse(property.Value.ToJsonString());
            var value = document.RootElement;
            switch (value.ValueKind)
            {
                case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
                case JsonValueKind.True: writer.WriteBooleanValue(true); break;
                case JsonValueKind.False: writer.WriteBooleanValue(false); break;
                case JsonValueKind.Number:
                    Require(value.TryGetInt64(out _) || ValidDecimal(value.GetRawText()), "Invalid numeric digest value.");
                    Require(value.TryGetDecimal(out var number), "Invalid numeric digest value.");
                    writer.WriteRawValue(number.ToString("G29", CultureInfo.InvariantCulture)); break;
                default: throw new ListException(ListFailure.Validation, "Only scalar record values are supported.");
            }
        }
        writer.WriteEndObject();
    }
}
