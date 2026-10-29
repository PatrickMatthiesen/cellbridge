using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class RecoveryJsonValidationTests
{
    [Theory]
    [InlineData("Etag", "access")]
    [InlineData("Authorization", "access")]
    [InlineData("Etag", "\\u0061ccess")]
    [InlineData("Authorization", "\\u0061ccess")]
    public async Task IgnoredHeadPropertiesCannotHideNestedDuplicateNames(string ignored, string repeatedName)
    {
        var provider = StorageTestDocuments.Memory();
        var head = (await new CellBridgeDocumentService(provider).CreateAsync("/duplicates.docx", MinimalDocx.Create(), TestActor.Value))!;
        var json = JsonSerializer.Serialize(head, RecoveryJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json[..^1] + "," + JsonSerializer.Serialize(ignored) +
            ":{\"nested\":{\"access\":1,\"" + repeatedName + "\":2}}}");
        var error = Assert.Throws<StorageCorruptionException>(() => RecoveryJson.Read<DocumentHead>(bytes));
        Assert.IsType<JsonException>(error.InnerException);
    }

    [Fact]
    public void ACustomConverterCannotSkipNestedDuplicateNames()
    {
        Assert.NotNull(RecoveryJson.Read<SkippedValue>("{\"nested\":{\"x\":1}}"u8));
        var error = Assert.Throws<StorageCorruptionException>(() => RecoveryJson.Read<SkippedValue>(
            "{\"nested\":{\"x\":1,\"x\":2}}"u8));
        Assert.IsType<JsonException>(error.InnerException);
    }

    public static IEnumerable<object[]> InvalidUtf8 =>
    [
        [new byte[] { (byte)'{', (byte)'"', 0xff, (byte)'"', (byte)':', (byte)'1', (byte)'}' }],
        [new byte[] { (byte)'"', 0xc0, 0xaf, (byte)'"' }],
        [new byte[] { (byte)'"', 0xe2, 0x82, (byte)'"' }],
        [new byte[] { (byte)'"', 0xed, 0xa0, 0x80, (byte)'"' }],
    ];

    [Theory]
    [MemberData(nameof(InvalidUtf8))]
    public void MalformedUtf8AlwaysProducesStorageCorruption(byte[] bytes)
    {
        var error = Assert.Throws<StorageCorruptionException>(() => RecoveryJson.Read<JsonElement>(bytes));
        Assert.IsType<DecoderFallbackException>(error.InnerException);
    }

    [Fact]
    public async Task IgnoredHeadPropertiesCannotHideMalformedUtf8Names()
    {
        var provider = StorageTestDocuments.Memory();
        var head = (await new CellBridgeDocumentService(provider).CreateAsync("/utf8.docx", MinimalDocx.Create(), TestActor.Value))!;
        var json = JsonSerializer.Serialize(head, RecoveryJson.Options);
        byte[] bytes = [.. Encoding.UTF8.GetBytes(json[..^1] + ",\"Authorization\":{\""), 0xff, .. "\":1}}"u8];
        var error = Assert.Throws<StorageCorruptionException>(() => RecoveryJson.Read<DocumentHead>(bytes));
        Assert.IsType<DecoderFallbackException>(error.InnerException);
    }

    [Fact]
    public async Task ValidRecoveryRoundTripPreservesDomainRecordsDictionariesAndCanonicalPackage()
    {
        var provider = StorageTestDocuments.Memory();
        var bytes = MinimalDocx.Create("valid recovery JSON");
        var head = (await new CellBridgeDocumentService(provider).CreateAsync("/round-trip.docx", bytes, TestActor.Value))!;
        await TestStorage.SetPermissionsAsync(provider, head.ResourceId, head.Security with
        { Grants = ImmutableDictionary<string, DocumentAccess>.Empty.Add("é", DocumentAccess.Read).Add("e\u0301", DocumentAccess.Write) });
        head = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
        var client = Guid.NewGuid().ToString();
        var joined = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(head.ResourceId, TestStorage.Admit(head),
            new() { Type = SubRequestType.EditorsTable, SubRequestDataAttributes =
                { ["EditorsTableRequestType"] = "JoinEditingSession", ["ClientID"] = client, ["Timeout"] = "3600", ["AsEditor"] = "true" } }));
        Assert.Null(joined.ErrorCode);
        var metadata = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(head.ResourceId,
            TestStorage.Admit(joined.Current.Head), new() { Type = SubRequestType.EditorsTable, SubRequestDataAttributes =
                { ["EditorsTableRequestType"] = "UpdateEditorMetadata", ["ClientID"] = client, ["Key"] = "Platform",
                    ["Value"] = Convert.ToBase64String("value é"u8) } }));
        Assert.Null(metadata.ErrorCode);
        var snapshot = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!;
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, RecoveryJson.Options);
        var decoded = RecoveryJson.Read<NormalizedDocumentSnapshot>(json);
        Assert.Equal(json, JsonSerializer.SerializeToUtf8Bytes(decoded, RecoveryJson.Options));
        Assert.Equal("value é"u8.ToArray(), Assert.Single(decoded.Current.Editors).Metadata["Platform"].ToArray());
        NormalizedDocumentRecovery.Validate(decoded, provider.Limits);
        await using var package = await new NormalizedSnapshotGraphSource(decoded, provider.Content, provider.Limits).PackageReader()
            .OpenReadAsync(decoded.Current.Head.Package);
        using var output = new MemoryStream(); await package.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
    }

    [Theory]
    [InlineData("bad\\uD800")]
    [InlineData("bad\\uDC00")]
    public async Task EscapedUnpairedSurrogateNamesInIgnoredFieldsProduceStorageCorruption(string name)
    {
        var provider = StorageTestDocuments.Memory();
        var head = (await new CellBridgeDocumentService(provider).CreateAsync("/surrogate.docx", MinimalDocx.Create(), TestActor.Value))!;
        var json = JsonSerializer.Serialize(head, RecoveryJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json[..^1] + ",\"Authorization\":{\"" + name + "\":1}}");
        var error = Assert.Throws<StorageCorruptionException>(() => RecoveryJson.Read<DocumentHead>(bytes));
        Assert.IsType<InvalidOperationException>(Assert.IsType<JsonException>(error.InnerException).InnerException);
    }

    [Fact]
    public async Task ValidEscapedSurrogatePairInIgnoredFieldRemainsReadable()
    {
        var provider = StorageTestDocuments.Memory();
        var head = (await new CellBridgeDocumentService(provider).CreateAsync("/valid-surrogate.docx", MinimalDocx.Create(), TestActor.Value))!;
        var json = JsonSerializer.Serialize(head, RecoveryJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json[..^1] + ",\"Authorization\":{\"bad\\uD800\\uDC00\":1}}");
        Assert.Equal(json, JsonSerializer.Serialize(RecoveryJson.Read<DocumentHead>(bytes), RecoveryJson.Options));
    }

    [Fact]
    public void ConverterImplementationFailuresAreNotClassifiedAsMalformedStorageJson()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RecoveryJson.Read<BrokenValue>("{}"u8));
        Assert.Equal("Converter failure.", error.Message);
    }

    [JsonConverter(typeof(BrokenConverter))]
    public sealed record BrokenValue;
    public sealed class BrokenConverter : JsonConverter<BrokenValue>
    {
        public override BrokenValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new InvalidOperationException("Converter failure.");
        public override void Write(Utf8JsonWriter writer, BrokenValue value, JsonSerializerOptions options) =>
            throw new InvalidOperationException("Converter failure.");
    }

    [JsonConverter(typeof(SkippingConverter))]
    public sealed record SkippedValue;
    public sealed class SkippingConverter : JsonConverter<SkippedValue>
    {
        public override SkippedValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        { reader.Skip(); return new(); }
        public override void Write(Utf8JsonWriter writer, SkippedValue value, JsonSerializerOptions options)
        { writer.WriteStartObject(); writer.WriteEndObject(); }
    }
}
