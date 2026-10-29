using System.Net;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CellBridge.Storage.Tests;

public sealed class SeparatePackageRetentionTests
{
    [Fact]
    public async Task RetainedPackagesSurviveDeletionAndRecreationWhilePublicReadsRejectTheRetiredIdentity()
    {
        var provider = StorageTestDocuments.Memory(); var service = new CellBridgeDocumentService(provider);
        var firstBytes = MinimalDocx.Create("original retained version");
        var initial = (await service.CreateAsync("/shared/retained.docx", firstBytes, TestActor.Value))!;
        var secondBytes = MinimalDocx.Create("second retained version");
        var saved = await RepositoryTestPublications.SaveAsync(service, initial, secondBytes);
        var retained = new[] { (initial.Package, Bytes: firstBytes), (saved.Package, Bytes: secondBytes) };
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddCellBridge(provider, requireDurability: false); TestActor.Register(builder.Services);
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge(); await app.StartAsync();
        using var client = TestActor.CreateClient(app.GetTestServer(), "writer");
        using (var before = await client.GetAsync(History(initial.Package)))
        { Assert.Equal(HttpStatusCode.OK, before.StatusCode); Assert.Equal(firstBytes, await before.Content.ReadAsByteArrayAsync()); }

        Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(saved.ResourceId,
            saved.LifecycleGeneration, saved.StateVersion))).Applied);
        await AssertRetainedAndDenied();
        using (var current = await client.GetAsync(initial.Path)) Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
        var retired = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId, true)))!;
        var replacementBytes = MinimalDocx.Create("new document incarnation");
        var replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), initial.Path, replacementBytes,
            initial.Security, provider.Content, provider.Limits, lifecycleGeneration: retired.LifecycleGeneration + 1);
        Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(retired.ResourceId,
            retired.LifecycleGeneration, retired.StateVersion, replacement))).Applied);
        await AssertRetainedAndDenied();
        using (var current = await client.GetAsync(initial.Path))
        { Assert.Equal(HttpStatusCode.OK, current.StatusCode); Assert.Equal(replacementBytes, await current.Content.ReadAsByteArrayAsync()); }
        Assert.Equal(replacement.Head.ResourceId, (await provider.Documents.FindAsync(new DocumentIdentity.Path(initial.PathKey)))!.ResourceId);
        await using (var current = await service.OpenDocumentAsync(new DocumentIdentity.Resource(replacement.Head.ResourceId), null, TestActor.Value))
            Assert.Equal(replacement.Head.ResourceId, current.Current.ResourceId);

        async Task AssertRetainedAndDenied()
        {
            Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)));
            Assert.Null(await provider.Documents.ReadControlAsync(initial.ResourceId));
            var deleted = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId, true)))!;
            var rejected = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(initial.ResourceId,
                TestStorage.Admit(deleted), new() { Type = SubRequestType.ServerTime }));
            Assert.False(rejected.Changed); Assert.Equal("FileNotFound", rejected.ErrorCode);
            Assert.Equal(deleted, rejected.Current.Head);
            Assert.Null(await provider.Documents.ReadRevisionsAsync(initial.ResourceId, 0, 10));
            Assert.Null(await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0)));
            Assert.Null(await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0) { StorageIndex = initial.Package.Root.StorageIndex }));
            foreach (var (package, bytes) in retained)
            {
                Assert.Equal(bytes, await TestStorage.ReadPackageAsync(provider, package));
                var selected = (await provider.Documents.ReadRevisionAsync(initial.ResourceId, package.RevisionNumber))!;
                Assert.True(selected.Current.IsDeleted); Assert.Equal(package, selected.Revision!.Package);
                var partition = (await provider.Documents.ReadPartitionAsync(initial.ResourceId,
                    new(0) { RevisionNumber = package.RevisionNumber, IncludeBindings = true }))!;
                Assert.True(partition.Current.IsDeleted); Assert.NotEmpty(partition.Bindings);
                Assert.Equal(package.Root.StorageIndex, partition.Partition.StorageIndex);
                await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetRevisionSummaryAsync(initial.ResourceId,
                    package.RevisionNumber, TestActor.Value).AsTask());
                await Assert.ThrowsAsync<KeyNotFoundException>(() => service.OpenDocumentAsync(new DocumentIdentity.Resource(initial.ResourceId),
                    package.RevisionNumber, TestActor.Value).AsTask());
                foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
                {
                    using var response = await client.SendAsync(new(method, History(package)));
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                }
            }
            Assert.Null((await provider.Documents.ReadRevisionAsync(initial.ResourceId, 999))!.Revision);
            Assert.Null(await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0) { RevisionNumber = 999 }));
            await Assert.ThrowsAsync<StorageCorruptionException>(() => provider.Packages.OpenReadAsync(initial.Package with { RevisionNumber = 999 }).AsTask());
            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetDocumentMetadataAsync(initial.ResourceId, TestActor.Value).AsTask());
        }
        static string History(DocumentPackage package) => $"/_cellbridge/history/{package.ResourceId:D}/{package.LifecycleGeneration}/{package.RevisionNumber}";
    }
}
