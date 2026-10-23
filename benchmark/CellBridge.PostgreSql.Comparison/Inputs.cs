using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.TestFixtures;

internal static class Settings
{
    public const string Method = "optimized-matched-long-lived-engine-v1";
    public const string SqlHead = "9827c29211ded20543546fe7696c8c28521189cb";
    public static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("CB_MEASUREMENT_ROOT")
        ?? throw new InvalidOperationException("Set CB_MEASUREMENT_ROOT to benchmark/CellBridge.PostgreSql.Comparison."));
    public static string[] Variants => (Environment.GetEnvironmentVariable("CB_BENCHMARK_VARIANTS") ?? "ef").Split(',');
    public static string Repo => Path.GetFullPath(Path.Combine(Root, "../.."));
    public static string Output => Path.GetFullPath(Environment.GetEnvironmentVariable("CB_MEASUREMENT_OUTPUT")
        ?? throw new InvalidOperationException("Set CB_MEASUREMENT_OUTPUT to a new output directory."));
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string JsonHash<T>(T value) => Hash(JsonSerializer.SerializeToUtf8Bytes(value));
    public static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
    public static void Validate()
    {
        Require(File.Exists(Path.Combine(Repo, "CellBridge.slnx")), "Measurement root does not identify this worktree.");
        Require(Output.StartsWith(Path.Combine(Repo, "artifacts") + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Output must be under this checkout's ignored artifacts/ directory.");
        // Reject overrides rather than accidentally comparing different JIT or GC conditions.
        foreach (var name in new[] { "DOTNET_TieredCompilation", "COMPlus_TieredCompilation", "DOTNET_TieredPGO",
                     "COMPlus_TieredPGO", "DOTNET_ReadyToRun", "COMPlus_ReadyToRun", "DOTNET_gcServer", "COMPlus_gcServer" })
            Require(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)), $"Unset {name}; this method uses runtime defaults.");
        foreach (var (file, hash) in new[] {
            ("benchmark/CellBridge.Storage.Benchmark/SyntheticWorkload.cs", "d40c341c220109ab3259191f08758b94f836169e02ec174d5ad59caa469ce609"),
            ("tests/CellBridge.TestFixtures/MinimalDocx.cs", "6aee1aee18613466717cec0d6fac0e346b67fe2ce72eba1190659aab7e724fe6") })
            Require(Hash(File.ReadAllBytes(Path.Combine(Repo, file))) == hash, $"Frozen generator changed: {file}");
        Require(Hash(File.ReadAllBytes(Path.Combine(Root, "expected-inputs.json"))) ==
            "2e3c451cb787ca6e842cdd3e542b5481347ef0d2713fc84bce8ee5f2ea0497cd", "Expected input identities changed.");
        Require(Variants.Length > 0 && Variants.Distinct().Count() == Variants.Length &&
            Variants.All(v => v is "ef" or "sql"), "CB_BENCHMARK_VARIANTS must be ef, sql, or sql,ef.");
        Directory.CreateDirectory(Output);
    }
}

internal static class Inputs
{
    public static readonly Guid Workspace = Guid.Parse("bb000000-0000-0000-0000-000000000001");
    public static readonly Guid List = Guid.Parse("bb000000-0000-0000-0000-000000000002");
    public static readonly Guid Title = Guid.Parse("ba000000-0000-0000-0000-000000000001");
    public static readonly Guid Counter = Guid.Parse("ba000000-0000-0000-0000-000000000002");
    public static readonly Guid PayloadField = Guid.Parse("ba000000-0000-0000-0000-000000000003");
    public static readonly ListActor Actor = new("benchmark:integration");
    public static readonly string Payload = string.Create(1024, new Random(42), static (text, random) =>
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        for (var i = 0; i < text.Length; i++) text[i] = alphabet[random.Next(alphabet.Length)];
    });
    public static JsonObject Values(int counter) => new()
    {
        [Title.ToString("D")] = "Prepared record", [Counter.ToString("D")] = counter,
        [PayloadField.ToString("D")] = Payload
    };
    public static Guid RecordId(int i) => Guid.Parse($"bc000000-0000-0000-0000-{i + 1:000000000000}");
    public static CreateListRequest ListRequest() => new(List, "Matched measurement", [
        new(Title, "Title", ListFieldType.Text), new(Counter, "Counter", ListFieldType.Integer),
        new(PayloadField, "Payload", ListFieldType.Text)]);
    public static async Task PrepareListAsync(Database database, IListEngine engine, int records)
    {
        using var identities = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Settings.Root, "expected-inputs.json")));
        Settings.Require(Settings.Hash(Encoding.UTF8.GetBytes(Payload)) ==
            identities.RootElement.GetProperty("lists").GetProperty("sha256").GetString(), "Lists payload differs from frozen input.");
        await database.RegisterWorkspaceAsync(Workspace, Actor.Subject);
        await engine.CreateListAsync(Actor, Workspace, ListRequest());
        for (var i = 0; i < records; i++)
        {
            var result = await engine.CreateRecordAsync(Actor, Workspace, List, new(RecordId(i), 1, Values(i)));
            Settings.Require(!result.Replayed && result.Revision == 1, "List preparation did not create a new record.");
        }
    }
    public static object Normalize(ListRecord record) => new
    {
        record.Id, record.SchemaVersion, record.Revision, record.Number, record.Values,
        record.CreatedBy, record.ModifiedBy
    };
    public static void VerifyPage(RecordPage page, int start)
    {
        Settings.Require(page.SchemaVersion == 1 && page.Records.Count == 100 && page.NextRecordId == RecordId(start + 99), "Invalid page shape/cursor.");
        for (var i = 0; i < page.Records.Count; i++)
        {
            var r = page.Records[i];
            Settings.Require(r.Id == RecordId(start + i) && r.Revision == 1 &&
                r.Values[Counter.ToString("D")]!.GetValue<long>() == start + i &&
                r.Values[PayloadField.ToString("D")]!.GetValue<string>() == Payload, "Incorrect page contents.");
        }
    }
    public static byte[] Content(int index)
    {
        var bytes = new byte[1024 * 1024];
        new Random(42 + index).NextBytes(bytes);
        return bytes;
    }
    // Copied verbatim from frozen Program.cs Package; linked MinimalDocx remains unchanged.
    public static byte[] Package(int sizeMiB, int seed)
    {
        var random = new Random(seed);
        using var initial = new MemoryStream(MinimalDocx.Create());
        using var existing = new ZipArchive(initial, ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in existing.Entries)
            {
                var copy = zip.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                copy.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var target = copy.Open();
                if (entry.FullName == "word/document.xml")
                {
                    using var writer = new StreamWriter(target);
                    writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>");
                    writer.Write(seed.ToString("X8"));
                    var buffer = new byte[4096];
                    for (long count = 0; count < sizeMiB * 1024L * 1024; count += buffer.Length * 2)
                    { random.NextBytes(buffer); writer.Write(Convert.ToHexString(buffer)); }
                    writer.Write("</w:t></w:r></w:p></w:body></w:document>");
                }
                else { using var input = entry.Open(); input.CopyTo(target); }
            }
        return output.ToArray();
    }
    public static byte[] VerifiedPackage(int seed)
    {
        var bytes = Package(1, seed);
        using var identities = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Settings.Root, "expected-inputs.json")));
        var expected = identities.RootElement.GetProperty("storage").EnumerateArray()
            .Single(x => x.GetProperty("sizeMiB").GetInt32() == 1 && x.GetProperty("seed").GetInt32() == seed);
        Settings.Require(bytes.Length == expected.GetProperty("bytes").GetInt32() &&
            Settings.Hash(bytes) == expected.GetProperty("sha256").GetString(), "DOCX differs from frozen input.");
        return bytes;
    }
}
