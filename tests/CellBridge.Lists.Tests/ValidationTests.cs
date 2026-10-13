using System.Text.Json.Nodes;
using CellBridge.Lists;

namespace CellBridge.Lists.Tests;

public class ValidationTests
{
    internal static readonly Guid Workspace = Guid.Parse("10000000-0000-0000-0000-000000000000");
    internal static readonly Guid List = Guid.Parse("20000000-0000-0000-0000-000000000000");
    internal static readonly Guid Field = Guid.Parse("30000000-0000-0000-0000-000000000000");
    internal static ListDefinition Definition(ListFieldType type = ListFieldType.Text, bool required = false) =>
        new(List, Workspace, "Orders", 1, [new ListField(Field, "Value", type, required)]);
    internal static JsonObject Values(string json) => new() { [Field.ToString("D")] = JsonNode.Parse(json) };
    internal static void Reject(Action action, ListFailure failure = ListFailure.Validation) => Assert.Equal(failure, Assert.Throws<ListException>(action).Failure);

    [Theory]
    [InlineData(ListFieldType.Text, "\"plain <script> text\"")]
    [InlineData(ListFieldType.Boolean, "true")]
    [InlineData(ListFieldType.Integer, "-9223372036854775808")]
    [InlineData(ListFieldType.Integer, "9223372036854775807")]
    [InlineData(ListFieldType.Decimal, "9999999999999999999999999999")]
    [InlineData(ListFieldType.Decimal, "0.000000000001")]
    [InlineData(ListFieldType.Decimal, "-1234567890123456.123456789012")]
    [InlineData(ListFieldType.Decimal, "1e3")]
    [InlineData(ListFieldType.Decimal, "1.00000000000000")]
    [InlineData(ListFieldType.Date, "\"2024-02-29\"")]
    [InlineData(ListFieldType.Timestamp, "\"2024-02-29T00:00:00Z\"")]
    [InlineData(ListFieldType.Timestamp, "\"2024-02-29T00:00:00.1234567Z\"")]
    public void Typed_boundaries_accept_exact_values(ListFieldType type, string json) =>
        ListValidation.ValidateValues(Definition(type, true), Values(json), new ListLimits());

    [Theory]
    [InlineData(ListFieldType.Integer, "9223372036854775808")]
    [InlineData(ListFieldType.Integer, "1.0")]
    [InlineData(ListFieldType.Integer, "\"1\"")]
    [InlineData(ListFieldType.Decimal, "10000000000000000000000000000")]
    [InlineData(ListFieldType.Decimal, "0.0000000000001")]
    [InlineData(ListFieldType.Decimal, "1.23456789012345678901234567891")]
    [InlineData(ListFieldType.Decimal, "1e-999999999")]
    [InlineData(ListFieldType.Date, "\"2023-02-29\"")]
    [InlineData(ListFieldType.Date, "\"2024-2-01\"")]
    [InlineData(ListFieldType.Timestamp, "\"2024-01-01T00:00:00+00:00\"")]
    [InlineData(ListFieldType.Timestamp, "\"2024-01-01T00:00:00.100Z\"")]
    [InlineData(ListFieldType.Timestamp, "\"2024-01-01T00:00:00.12345678Z\"")]
    [InlineData(ListFieldType.Boolean, "1")]
    [InlineData(ListFieldType.Text, "{}")]
    [InlineData(ListFieldType.Text, "[]")]
    public void Wrong_types_and_precision_are_rejected(ListFieldType type, string json) =>
        Reject(() => ListValidation.ValidateValues(Definition(type), Values(json), new ListLimits()));

    [Fact]
    public void Text_round_trips_unicode_and_rejects_postgresql_nul_or_unpaired_surrogates()
    {
        foreach (var text in new[] { "ordinary text", "emoji \U0001F600", "line\nnext\ttab", "replacement \ufffd" })
            ListValidation.ValidateValues(Definition(), new JsonObject { [Field.ToString("D")] = text }, new ListLimits());
        foreach (var text in new[] { "bad\0value", "bad\ud800value", "bad\udfffvalue" })
            Reject(() => ListValidation.ValidateValues(Definition(), new JsonObject { [Field.ToString("D")] = text }, new ListLimits()));
        Reject(() => ListValidation.ValidateValues(Definition(), Values("\"bad\\u0000value\""), new ListLimits()));
        Reject(() => ListValidation.ValidateDefinition(Definition() with { Name = "bad\ud800name" }, new ListLimits()));
    }

    [Fact]
    public void Optional_absence_and_null_are_distinct_and_required_rejects_both()
    {
        ListValidation.ValidateValues(Definition(), new JsonObject(), new ListLimits());
        ListValidation.ValidateValues(Definition(), Values("null"), new ListLimits());
        Reject(() => ListValidation.ValidateValues(Definition(required: true), new JsonObject(), new ListLimits()));
        Reject(() => ListValidation.ValidateValues(Definition(required: true), Values("null"), new ListLimits()));
    }

    [Fact]
    public void Unknown_noncanonical_and_duplicate_changes_fail_explicitly()
    {
        var unknown = new JsonObject { [Guid.NewGuid().ToString("D")] = "x" };
        Reject(() => ListValidation.ValidateValues(Definition(), unknown, new ListLimits()));
        var uppercaseId = Guid.Parse("a0000000-0000-0000-0000-000000000000");
        var definition = Definition() with { Fields = [new ListField(uppercaseId, "Value", ListFieldType.Text)] };
        Reject(() => ListValidation.ValidateValues(definition, new JsonObject { [uppercaseId.ToString("D").ToUpperInvariant()] = "x" }, new ListLimits()));
        Reject(() => ListValidation.ApplyChanges(Definition(), new JsonObject(), [new(Field, JsonValue.Create("x")), new(Field, Remove: true)], new ListLimits()));
        Reject(() => ListValidation.ApplyChanges(Definition(), new JsonObject(), [new(Field, JsonValue.Create("x"), true)], new ListLimits()));
    }

    [Fact]
    public void Patches_clone_values_and_ignore_display_authority()
    {
        var definition = Definition() with { Fields = [new ListField(Field, "Value", ListFieldType.Text, Display: new(Hidden: true, ReadOnly: true))] };
        var original = Values("\"old\"");
        var next = ListValidation.ApplyChanges(definition, original, [new(Field, JsonValue.Create("new"))], new ListLimits());
        Assert.Equal("old", original[Field.ToString("D")]!.GetValue<string>());
        Assert.Equal("new", next[Field.ToString("D")]!.GetValue<string>());
        Assert.Empty(ListValidation.ApplyChanges(definition, next, [new(Field, Remove: true)], new ListLimits()));
        Reject(() => ListValidation.ApplyChanges(Definition(required: true), original, [new(Field, Remove: true)], new ListLimits()));
    }

    [Fact]
    public void Limits_count_utf8_bytes_and_bound_metadata()
    {
        var values = Values("\"☃☃☃\"");
        Reject(() => ListValidation.ValidateValues(Definition(), values, new ListLimits { MaxRecordBytes = 40 }), ListFailure.Limit);
        Reject(() => ListValidation.ValidateDefinition(Definition(), new ListLimits { MaxFields = 0 }), ListFailure.Limit);
        Reject(() => ListValidation.ValidateDefinition(Definition() with { Fields = [new ListField(Field, "V", ListFieldType.Text, Display: new(Tags: Enumerable.Repeat("x", 17).ToArray()))] }, new ListLimits()));
        Reject(() => ListValidation.ValidateDefinition(Definition() with { Name = "bad\u0000name" }, new ListLimits()));
    }

    [Fact]
    public void Choice_ids_and_reference_targets_are_checked()
    {
        var choice = Definition() with { Fields = [new ListField(Field, "Stage", ListFieldType.Choice, Choices: [new("todo", "To do")])] };
        ListValidation.ValidateDefinition(choice, new ListLimits());
        ListValidation.ValidateValues(choice, Values("\"todo\""), new ListLimits());
        Reject(() => ListValidation.ValidateValues(choice, Values("\"To do\""), new ListLimits()));
        var reference = Definition() with { Fields = [new ListField(Field, "Owner", ListFieldType.Reference, ReferenceListId: List)] };
        ListValidation.ValidateDefinition(reference, new ListLimits());
        ListValidation.ValidateValues(reference, Values("\"10000000-0000-0000-0000-000000000000\""), new ListLimits());
        Reject(() => ListValidation.ValidateValues(reference, Values("\"00000000-0000-0000-0000-000000000000\""), new ListLimits()));
        Reject(() => ListValidation.ValidateDefinition(reference with { Fields = [reference.Fields[0] with { ReferenceListId = null }] }, new ListLimits()));
    }

    [Fact]
    public void Canonical_digest_handles_property_order_numeric_spellings_and_null_presence()
    {
        var other = Guid.NewGuid().ToString("D");
        var first = Values("1.000"); first[other] = "é";
        var second = new JsonObject { [other] = "é", [Field.ToString("D")] = JsonNode.Parse("1e0") };
        var request = new CreateRecordRequest(List, 1, first);
        Assert.Equal(ListValidation.CreateDigest(request), ListValidation.CreateDigest(request with { Values = second }));
        Assert.NotEqual(ListValidation.CreateDigest(request with { Values = new() }), ListValidation.CreateDigest(request with { Values = Values("null") }));
        Assert.NotEqual(ListValidation.CreateDigest(request), ListValidation.CreateDigest(request with { SchemaVersion = 2 }));
        Assert.NotEqual(ListValidation.CreateDigest(request), ListValidation.CreateDigest(request with { Id = Workspace }));
        Reject(() => ListValidation.CreateDigest(request with { Values = Values("1.00000000000000000000000000001") }));
    }
}
