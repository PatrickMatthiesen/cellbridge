using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Benchmarks.Tests;

public sealed class AcknowledgedRevisionVerifierTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsIncorrectOrDuplicatedHistoryEvenWhenTheFinalPackageMatches(bool duplicate)
    {
        var provider = BenchmarkTestDocuments.Memory();
        var actor = new CellBridgeActor(new SubjectIdentity("verifier:test", "test", "Test"), CanCreate: true);
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/verify.docx", MinimalDocx.Create("initial"), actor))!;
        var head = initial;
        var expected = new Dictionary<ExGuid, string>();
        for (var number = 0; number < 4; number++)
        {
            var bytes = MinimalDocx.Create("revision " + number);
            var graph = await BenchmarkTestDocuments.GraphAsync(service, head, actor);
            var request = SyntheticWorkload.Save(graph, bytes, "full", number + 1, new Random(number));
            var result = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), actor);
            Assert.All(result.Response.SubResponses, response => Assert.False(response.Status));
            head = result.State;
            expected.Add(StorageIds.Restore(head.Package.Root.StorageIndex), Convert.ToHexString(SHA256.HashData(bytes)));
        }
        var revisions = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!.ApplicationRevisions;
        await AcknowledgedRevisionVerifier.VerifyAsync(provider, revisions, initial.LatestRevisionNumber, expected);
        var historical = revisions.Single(revision => revision.RevisionNumber == initial.LatestRevisionNumber + 1);
        var inspected = revisions.ToArray();
        if (duplicate)
        {
            // A,B,A,D has the expected count and correct final bytes, but omits C.
            var index = Array.FindIndex(inspected, revision => revision.RevisionNumber == initial.LatestRevisionNumber + 3);
            inspected[index] = historical;
        }
        else expected[StorageIds.Restore(historical.Package.Root.StorageIndex)] = new string('0', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => AcknowledgedRevisionVerifier.VerifyAsync(
            provider, inspected, initial.LatestRevisionNumber, expected));
    }
}
