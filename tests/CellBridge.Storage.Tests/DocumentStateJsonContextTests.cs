using System.Runtime.CompilerServices;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class DocumentStateJsonContextTests
{
    [Fact]
    public async Task ConcurrentCodecOperationsAndFailuresCannotReuseAnotherDocumentsTable()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var first = (await service.CreateAsync("/codec-first.docx", MinimalDocx.Create("first"), TestActor.Value))!;
        var second = await RevisionHistoryTests.Save(service,
            (await service.CreateAsync("/codec-second.docx", MinimalDocx.Create("second"), TestActor.Value))!, "second revision");
        var expected = new[] { JsonSerializer.Serialize(first), JsonSerializer.Serialize(second) };
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
        {
            var state = index % 2 == 0 ? first : second;
            var json = DocumentStateJson.Serialize(state);
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(json), DocumentStateJson.GetSerializedSize(state));
            Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(
                json.Replace("\"Elements\":[0,", "\"Elements\":[-1,", StringComparison.Ordinal)));
            var decoded = DocumentStateJson.Deserialize(json, strict: index % 3 == 0);
            Assert.Equal(expected[index % 2], JsonSerializer.Serialize(decoded));
        });
    }

    [Fact]
    public void CachedSerializerMetadataAndSizeCountsDoNotRetainDocumentGraphs()
    {
        var references = ExerciseCodec();
        // This diagnostic verifies cache ownership, not a production collection policy.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseCodec()
    {
        var content = new InMemoryContentStore();
        var state = new DocumentStore().Put("/codec-ownership.docx", MinimalDocx.Create())
            .CaptureAsync(content).AsTask().GetAwaiter().GetResult();
        _ = DocumentStateJson.GetSerializedSize(state);
        var decoded = DocumentStateJson.Deserialize(DocumentStateJson.Serialize(state), strict: true);
        return [new(state), new(state.Partitions[0]), new(state.Partitions[0].Elements[0]),
            new(decoded), new(decoded.Partitions[0].Elements[0])];
    }
}
