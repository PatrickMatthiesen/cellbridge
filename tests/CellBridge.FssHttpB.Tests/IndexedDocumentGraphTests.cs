using System.Collections.Immutable;
using System.Security.Cryptography;
using System.IO.Compression;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Tests;

public sealed class IndexedDocumentGraphTests
{
    private static readonly IndexedGraphScope Scope = new(Guid.Parse("c07ed088-ddbf-46a5-bdf3-dd0ae988a9d5"), 1, 0);
    private static ExtendedId Id(uint value) => IndexedDocumentGraph.Capture(GraphFixture.Id(value));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvedBindingsPreserveInheritedBytesAndExcludeOtherCellsAndObjectPartitions(bool blob)
    {
        var fixture = new GraphFixture("exact inherited bytes"u8.ToArray(), blob);
        var source = new Source(fixture);
        var cell = IndexedDocumentGraph.Capture(fixture.Cell);
        var resolved = await IndexedDocumentGraph.ResolveFileCellAsync(Scope, Id(1), cell, Id(20), source);
        Assert.Equal(3, resolved.Bindings.Length);
        Assert.Contains(resolved.Bindings, binding => binding.Key.ObjectId == Id(30) && binding.Key.ObjectPartitionId == 4);
        Assert.All(resolved.Bindings, binding => Assert.Equal(cell, binding.Key.Cell));
        var root = new DocumentPackageRoot(Scope, Id(1), cell, Id(20), Id(30));
        var loaded = new HashSet<ExtendedId>();
        var graph = await IndexedDocumentGraph.LoadFileAsync(root, resolved.Bindings, ReadElement);
        Assert.Equal(fixture.File().Materialize(), graph.Materialize());
        Assert.DoesNotContain(Id(11), loaded);
        Assert.DoesNotContain(Id(4), loaded);
        Assert.Equal(blob, loaded.Contains(Id(8)));

        ValueTask<DataElement> ReadElement(IndexedGraphScope scope, ExtendedId id, CancellationToken ct)
        {
            Assert.Equal(Scope, scope);
            loaded.Add(id);
            return ValueTask.FromResult(fixture.Elements.Single(element => IndexedDocumentGraph.Capture(element.DataElementExtendedGuid) == id));
        }
    }

    [Fact]
    public async Task ColdPackageReaderVerifiesExactBytesAndRejectsWrongDigest()
    {
        var fixture = new GraphFixture("package bytes with fixed layout"u8.ToArray(), blob: true);
        var source = new Source(fixture);
        var cell = IndexedDocumentGraph.Capture(fixture.Cell);
        var resolved = await IndexedDocumentGraph.ResolveFileCellAsync(Scope, Id(1), cell, Id(20), source);
        var descriptor = new DocumentPackage(Scope.ResourceId, 1, 7, 1,
            new(Scope, Id(1), cell, Id(20), Id(30)), fixture.Bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(fixture.Bytes)));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reader = Reader();
            await using var result = await reader.OpenReadAsync(descriptor);
            Assert.True(result.CanSeek);
            Assert.Equal(0, result.Position);
            using var actual = new MemoryStream();
            await result.CopyToAsync(actual);
            Assert.Equal(fixture.Bytes, actual.ToArray());
        }
        await Assert.ThrowsAsync<StorageCorruptionException>(async () =>
            await Reader().OpenReadAsync(descriptor with { Sha256 = new string('0', 64) }));

        GraphDocumentPackageReader Reader() => new(
            (_, _) => ValueTask.FromResult<IReadOnlyList<IndexedObjectBinding>>(resolved.Bindings),
            (_, id, _) => ValueTask.FromResult(fixture.Elements.Single(element => IndexedDocumentGraph.Capture(element.DataElementExtendedGuid) == id)),
            1024);
    }

    [Fact]
    public async Task LegacySelfContainedFileDoesNotRequireAnUnusedUnmappedAncestor()
    {
        var fixture = new GraphFixture([1, 2, 3]);
        fixture.MakeSelfContainedWithUnmappedAncestor();
        var source = new Source(fixture);
        var resolved = await IndexedDocumentGraph.ResolveFileCellAsync(Scope, Id(1),
            IndexedDocumentGraph.Capture(fixture.Cell), Id(20), source);
        var root = new DocumentPackageRoot(Scope, Id(1), IndexedDocumentGraph.Capture(fixture.Cell), Id(20), Id(30));
        var graph = await IndexedDocumentGraph.LoadFileAsync(root, resolved.Bindings,
            (_, id, _) => ValueTask.FromResult(fixture.Elements.Single(element => IndexedDocumentGraph.Capture(element.DataElementExtendedGuid) == id)));
        Assert.Equal(fixture.File().Materialize(), graph.Materialize());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await IndexedDocumentGraph.ResolveCellAsync(Scope,
            Id(1), IndexedDocumentGraph.Capture(fixture.Cell), Id(20), source));
    }

    [Fact]
    public async Task RestoreWrapperCanInheritACompleteLegacyFileWithAnUnmappedUnusedBase()
    {
        var fixture = new GraphFixture("legacy restored bytes"u8.ToArray());
        fixture.MakeSelfContainedWithUnmappedAncestor();
        var source = new Source(fixture);
        source.Add(GraphFixture.Element(DataElementType.RevisionManifestDataElementData, GraphFixture.Id(50), writer =>
        {
            GraphFixture.Record(writer, StreamObjectTypeHeaderStart.RevisionManifest, body =>
            { GraphFixture.Id(51).Serialize(body); fixture.Revision.Serialize(body); });
            GraphFixture.Record(writer, StreamObjectTypeHeaderStart.RevisionManifestRootDeclare, body =>
            { fixture.Root.Serialize(body); fixture.RootObject.Serialize(body); });
        }));
        source.Mappings.Add(new(Scope, Id(1), new(IndexedMappingKind.Revision, Revision: Id(51)), Id(50), new(Guid.NewGuid(), 1)));
        var cell = IndexedDocumentGraph.Capture(fixture.Cell);
        var restored = await IndexedDocumentGraph.ResolveFileCellAsync(Scope, Id(1), cell, Id(51), source);
        var graph = await IndexedDocumentGraph.LoadFileAsync(new(Scope, Id(1), cell, Id(51), Id(30)), restored.Bindings,
            (_, id, _) => ValueTask.FromResult(fixture.Elements.Single(element => IndexedDocumentGraph.Capture(element.DataElementExtendedGuid) == id)));
        Assert.Equal(fixture.Bytes, graph.Materialize());
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await IndexedDocumentGraph.ResolveCellAsync(Scope, Id(1), cell, Id(51), source));
    }

    [Fact]
    public async Task BranchFromOlderBaseRevertsAnObjectChangedOnlyOnPreviousHead()
    {
        var fixture = new GraphFixture("x0"u8.ToArray());
        fixture.OverrideChild("x1"u8.ToArray());
        var source = new Source(fixture);
        var cell = IndexedDocumentGraph.Capture(fixture.Cell);
        var oldHead = await IndexedDocumentGraph.ResolveCellAsync(Scope, Id(1), cell, Id(20), source);
        // C branches from A (21), not B (20). Its own group adds Y but does not redefine X (31).
        source.Add(GraphFixture.Element(DataElementType.RevisionManifestDataElementData, GraphFixture.Id(50), writer =>
        {
            GraphFixture.Record(writer, StreamObjectTypeHeaderStart.RevisionManifest, body =>
            { GraphFixture.Id(51).Serialize(body); GraphFixture.Id(21).Serialize(body); });
            GraphFixture.Record(writer, StreamObjectTypeHeaderStart.RevisionManifestRootDeclare, body =>
            { fixture.Root.Serialize(body); fixture.RootObject.Serialize(body); });
            GraphFixture.Record(writer, StreamObjectTypeHeaderStart.RevisionManifestObjectGroupReferences, GraphFixture.Id(52).Serialize);
        }));
        source.Add(GraphFixture.GroupWithForeignObject(GraphFixture.Id(52), fixture.RootObject, GraphFixture.Id(53)));
        source.Mappings.Add(new(Scope, Id(1), new(IndexedMappingKind.Revision, Revision: Id(51)), Id(50), new(Guid.NewGuid(), 1)));
        var branch = await IndexedDocumentGraph.ResolveCellAsync(Scope, Id(1), cell, Id(51), source);
        Assert.Equal(Id(6), oldHead.Bindings.Single(binding => binding.Key.ObjectId == Id(31)).GroupElement);
        Assert.Equal(Id(7), branch.Bindings.Single(binding => binding.Key.ObjectId == Id(31)).GroupElement);
        var delta = IndexedDocumentGraph.Difference(Scope, 10, 11, oldHead.Bindings, branch.Bindings);
        Assert.Contains(delta.Changes, change => change.Key.ObjectId == Id(31) && change.Value?.GroupElement == Id(7));
        Assert.Contains(delta.Changes, change => change.Key.ObjectId == Id(30) && change.Key.ObjectPartitionId == 4 && change.Value is null);
        var checkpoint = IndexedDocumentGraph.Checkpoint(Scope, 10, oldHead.Bindings);
        var replayed = IndexedDocumentGraph.Replay(checkpoint, [delta], 11, new());
        Assert.Equal(branch.Bindings.OrderBy(b => b.Key.ObjectId.Value), replayed.OrderBy(b => b.Key.ObjectId.Value));
    }

    [Fact]
    public async Task SiblingDefinitionsCannotResolveReferencesInAnotherRevisionChain()
    {
        var fixture = new GraphFixture([1]);
        fixture.Replace(GraphFixture.GroupWithForeignObject(fixture.BaseGroup, fixture.RootObject, GraphFixture.Id(89)));
        var source = new Source(fixture);
        // Child 31 still exists in another cell's group, which must not satisfy this cell.
        await Assert.ThrowsAsync<InvalidDataException>(async () => await IndexedDocumentGraph.ResolveCellAsync(Scope,
            Id(1), IndexedDocumentGraph.Capture(fixture.Cell), Id(20), source));
    }

    [Fact]
    public async Task IndexedResolverRejectsRevisionCycleAndWrongScope()
    {
        var fixture = new GraphFixture([1]);
        fixture.SetBase(fixture.Revision);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await IndexedDocumentGraph.ResolveCellAsync(Scope,
            Id(1), IndexedDocumentGraph.Capture(fixture.Cell), Id(20), new Source(fixture)));
        var wrongScope = Scope with { LifecycleGeneration = 2 };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await IndexedDocumentGraph.ResolveCellAsync(wrongScope,
            Id(1), IndexedDocumentGraph.Capture(fixture.Cell), Id(20), new Source(new([1]))));
    }

    [Fact]
    public void CheckpointEnforcesBothReplayBoundsAndExactPublicationChain()
    {
        var cell = new IndexedCellId(Id(40), Id(41));
        var first = new IndexedObjectBinding(new(Scope, cell, Id(30), 1), Id(6), 0);
        var next = first with { GroupElement = Id(7) };
        var checkpoint = IndexedDocumentGraph.Checkpoint(Scope, 1, [first]);
        var delta = IndexedDocumentGraph.Difference(Scope, 1, 2, [first], [next]);
        Assert.True(IndexedDocumentGraph.RequiresCheckpoint(new(1, 0, 0), delta, new(1, long.MaxValue)));
        Assert.True(IndexedDocumentGraph.RequiresCheckpoint(new(1, 0, 0), delta, new(128, delta.EncodedBytes)));
        Assert.Throws<InvalidDataException>(() => IndexedDocumentGraph.Replay(checkpoint, [delta], 2, new(128, delta.EncodedBytes - 1)));
        Assert.Throws<InvalidDataException>(() => IndexedDocumentGraph.Replay(checkpoint, [delta with { PreviousSequence = 0 }], 2, new()));
        Assert.Throws<InvalidDataException>(() => IndexedDocumentGraph.Replay(checkpoint, [delta with { EncodedBytes = 0 }], 2, new()));
        Assert.Throws<InvalidDataException>(() => IndexedDocumentGraph.Replay(checkpoint, [], 2, new()));
        Assert.Throws<InvalidDataException>(() => IndexedDocumentGraph.Difference(Scope, 1, 2, [],
            [first with { Key = first.Key with { Scope = Scope with { TargetPartition = 1 } } }]));
    }

    [Fact]
    public void ImmutableProtocolIdentityAllowsDifferentStorageKeysButRejectsChangedSerialOrBytes()
    {
        var original = new GraphElementState(Id(1), 1, new(Guid.NewGuid(), 3), new("first", 3, "digest")) { MappingSerials = [] };
        IndexedDocumentGraph.ValidateIdentity(original, original with { Payload = original.Payload with { Key = "second" } });
        Assert.Throws<InvalidDataException>(() => IndexedDocumentGraph.ValidateIdentity(original, original with { Serial = original.Serial with { Value = 4 } }));
        Assert.Throws<InvalidDataException>(() => IndexedDocumentGraph.ValidateIdentity(original, original with { Payload = original.Payload with { Sha256 = "other" } }));
    }

    [Fact]
    public async Task MigrationPreservesHistoryReceiptsDeliveriesAndCurrentIdentityWithoutWritingContent()
    {
        var fixture = new GraphFixture("migrated exact bytes"u8.ToArray(), blob: true);
        var content = new TestContent();
        var package = await content.WriteAsync(new MemoryStream(fixture.Bytes));
        var empty = await content.WriteAsync(new MemoryStream([]));
        var response = await content.WriteAsync(new MemoryStream([9, 8, 7]));
        var elements = ImmutableArray.CreateBuilder<GraphElementState>();
        foreach (var element in fixture.Elements)
            elements.Add(new(IndexedDocumentGraph.Capture(element.DataElementExtendedGuid), (uint)element.DataElementType,
                new(element.SerialNumber.Guid, element.SerialNumber.Value), await content.WriteAsync(new MemoryStream(element.Data!)))
            {
                MappingSerials = element.DataElementType == DataElementType.StorageIndexDataElementData
                    ? StorageIndexMappingSerials.Read(element.Data!).Select(serial => new SerialId(serial.Guid, serial.Value)).ToImmutableArray() : [],
            });
        var identity = new PartitionIdentity(Id(2), Id(3), Id(4), Id(6), Id(8), Id(30), Id(20), Id(40), Id(41), Guid.NewGuid());
        var file = new PartitionState(0, identity, 2000, package, Id(1), elements.ToImmutable(), []);
        var metadata = new PartitionState(1, identity, 2001, empty, null, [], [1]);
        var editors = new PartitionState(2, identity, 2002, empty, null, [], [2]);
        var author = new SubjectIdentity("owner", "owner", "Owner");
        var now = DateTime.UtcNow;
        var state = new DocumentState(2, Scope.ResourceId, "/file.docx", "/FILE.DOCX", now, now, 2, 25,
            package, [file, metadata, editors], [], CoordinationState.Empty,
            [new("save", "digest", 1, response, author.Subject)])
        {
            Security = DocumentSecurity.Create(author), RetiredPathKeys = ["/RETIRED.DOCX"],
            Revisions = [new(Scope.ResourceId, 1, 1, 1, now, author, package, [file, metadata]),
                new(Scope.ResourceId, 1, 2, 2, now, author, package, [file, metadata])],
            RestoreReceipts = [new(1, author.Subject, "restore", "restore-digest", 2)],
            WholeFileSaveReceipts = [new(1, author.Subject, "whole", "whole-digest", 2)],
            Publication = new(Guid.NewGuid(), "destination", "external-version", 2,
                [new(Guid.NewGuid(), Scope.ResourceId, 1, 1, 1, package)]),
        };
        var writes = content.Writes;
        var result = await LegacyDocumentProjection.PrepareAsync(state, content, new());
        Assert.Equal(writes, content.Writes);
        Assert.Equal(2, result.Revisions.Length);
        Assert.Equal(3, result.Receipts.Length);
        Assert.Equal(25, result.Current.Head.StateVersion);
        Assert.Equal(state.RetiredPathKeys, result.RetiredPathKeys);
        Assert.Equal(1UL, Assert.Single(result.Deliveries).Package.RevisionNumber);
        Assert.Equal(3, result.Current.Partitions.Length);
        Assert.All(result.Revisions.SelectMany(revision => revision.Partitions), partition => Assert.NotNull(partition.Checkpoint));
        Assert.Single(result.LegacyPackageReferences);
        Assert.Contains(response, result.PhysicalReferences);
        Assert.Equal(package.Sha256, result.Current.Head.Package.Sha256);
        Assert.DoesNotContain(result.Current.Partitions.SelectMany(partition => partition.AddedElements), element => element.Type == (uint)DataElementType.StorageIndexDataElementData);
        Assert.Equal(result.Revisions[^1].Revision.Package, result.Current.Head.Package);
        Assert.Equal(3, result.Revisions.Select(revision => revision.Revision.Package.Root.StorageIndex)
            .Append(result.Current.Partitions.Single(partition => partition.After.Kind == 0).After.StorageIndex!).Distinct().Count());
        await Assert.ThrowsAsync<StorageCorruptionException>(async () =>
            await LegacyDocumentProjection.PrepareAsync(state with { Content = package with { Sha256 = new string('0', 64) } }, content, new()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStoresOnlyCanonicalElementsAndAnEmptyMappingPatchPreservesTheFile(bool restore)
    {
        var content = new TestContent();
        var owner = new SubjectIdentity("owner", "owner", "Owner");
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in new[] { "[Content_Types].xml", "word/document.xml" })
            {
                using var entry = new StreamWriter(zip.CreateEntry(name).Open());
                entry.Write("<root />");
            }
        var bytes = buffer.ToArray();
        var created = await IndexedDocumentPublicationBuilder.CreateAsync(Scope.ResourceId, "/file.docx", bytes,
            DocumentSecurity.Create(owner), content, new());
        var file = created.Partitions.Single(partition => partition.After.Kind == 0);
        Assert.DoesNotContain(file.AddedElements, element => element.Type == (uint)DataElementType.StorageIndexDataElementData);
        Assert.Equal(file.AddedElements.Length, content.Writes);
        Assert.NotNull(file.After.StorageIndexSerial);
        Assert.NotEmpty(file.MappingChanges);
        var source = new SaveSource(file);
        var read = new SavePreparationRead(new(created.Head, DateTime.UtcNow, CoordinationState.Empty, [],
                created.Partitions.Select(partition => partition.After).ToImmutableArray()),
            new(created.Head, file.After, [], file.Checkpoint!.Bindings), null) { ReplayPosition = new(1, 0, 0) };
        var index = new ExGuid(1, Guid.NewGuid());
        var request = new FsshttpbCellSubRequest(RequestTypes.PutChanges) { RequestId = 7,
            Data = new PutChangesSubRequestData { StorageIndex = index, AdditionalFlagsBits = 1 } };
        var upload = new DataElementPackage();
        upload.DataElements.Add(new(DataElementType.StorageIndexDataElementData, index, SerialNumber.Null) { Data = [] });
        var writes = content.Writes;
        var prepared = await IndexedPartitionSavePreparer.PrepareAsync(read, request, upload,
            new(owner, Scope.ResourceId, 1, null, DocumentAccess.Read | DocumentAccess.Write)
                { AuthorityGeneration = created.Head.AuthorityGeneration, StateVersion = created.Head.StateVersion },
            ImmutableDictionary<string, string>.Empty, source, content, new(), writeContext: restore
                ? new(new(1, SaveOperationFamily.Restore, 0, "restore"), new string('a', 64)) : null);
        Assert.Null(prepared.ErrorCode);
        Assert.False(Assert.Single(prepared.Response.SubResponses).Status);
        var publication = Assert.IsType<PreparedDocumentPublication>(prepared.Publication);
        var changed = Assert.Single(publication.Partitions,part => part.After.Kind == 0);
        Assert.Empty(changed.MappingChanges);
        Assert.Empty(changed.AddedElements);
        Assert.Empty(changed.EffectiveChanges.Changes);
        var responseData = Assert.IsType<PutChangesSubResponseData>(Assert.Single(prepared.Response.SubResponses).Data);
        var acceptedKnowledge = ClientKnowledge.Deserialize(new(responseData.KnowledgeBytes!));
        Assert.All(file.AddedElements, element => Assert.False(acceptedKnowledge.Contains(new(element.Serial.Guid, element.Serial.Value))));
        Assert.True(acceptedKnowledge.Contains(new(changed.After.StorageIndexSerial!.Guid, changed.After.StorageIndexSerial.Value)));
        Assert.Equal(created.Head.Package.Sha256, publication.Package!.Sha256);
        Assert.Equal(created.Head.Package.Length, publication.Package.Length);
        Assert.Equal(writes + (restore ? 0 : 1), content.Writes); // Only protocol saves store a binary retry response.
        Assert.Equal(file.After.StorageIndex, source.RequestedIndexes.Distinct().Single());
        Assert.NotEqual(IndexedDocumentGraph.Capture(index), changed.After.StorageIndex);
        Assert.Equal(changed.After.StorageIndex, IndexedDocumentGraph.Capture(Assert.Single(prepared.Response.DataElementPackage!.DataElements).DataElementExtendedGuid));
        Assert.Equal(changed.After.StorageIndex, IndexedDocumentGraph.Capture(responseData.PutChangesResponse!.AppliedStorageIndexID));
        Assert.DoesNotContain(publication.NewDocumentReferences, handle => handle.Sha256 == created.Head.Package.Sha256);
        Assert.NotEmpty(source.RequestedGroups);
        Assert.NotEmpty(source.RequestedRevisions);
        Assert.All(source.RequestedGroups.Select(value => (Kind: "group", Read: value))
            .Concat(source.RequestedRevisions.Select(value => (Kind: "revision", Read: value)))
            .GroupBy(value => value), reads => Assert.Single(reads));
    }

    [Fact]
    public async Task OversizedReceiptFailsBeforeStagingAnyPhysicalContent()
    {
        var content = new TestContent();
        var owner = new SubjectIdentity("owner", "owner", "Owner");
        var created = await IndexedDocumentPublicationBuilder.CreateAsync(Scope.ResourceId, "/file.docx", "initial"u8.ToArray(),
            DocumentSecurity.Create(owner), content, new());
        var metadata = created.Partitions.Single(partition => partition.After.Kind == 1);
        var read = new SavePreparationRead(new(created.Head, DateTime.UtcNow, CoordinationState.Empty, [],
                created.Partitions.Select(partition => partition.After).ToImmutableArray()),
            new(created.Head, metadata.After, [], []), null) { ReplayPosition = new(1, 0, 0) };
        var index = new ExGuid(1, Guid.NewGuid());
        var request = new FsshttpbCellSubRequest(RequestTypes.PutChanges) { RequestId = 1,
            Data = new PutChangesSubRequestData { StorageIndex = index } };
        var upload = new DataElementPackage();
        upload.DataElements.Add(new(DataElementType.StorageIndexDataElementData, index, SerialNumber.Null) { Data = [] });
        var writes = content.Writes;
        var error = await Assert.ThrowsAsync<StorageQuotaExceededException>(async () =>
            await IndexedPartitionSavePreparer.PrepareAsync(read, request, upload,
                new(owner, Scope.ResourceId, 1, null, DocumentAccess.Read | DocumentAccess.Write)
                    { AuthorityGeneration = created.Head.AuthorityGeneration, StateVersion = created.Head.StateVersion },
                ImmutableDictionary<string, string>.Empty, new SaveSource(metadata), content, new() { MaxObjectBytes = 1 }));
        Assert.Equal("save receipt bytes", error.Budget);
        Assert.Equal(writes, content.Writes);
        Assert.Equal(0, content.Batches);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "failure")]
    [InlineData(false, "cancel")]
    [InlineData(false, "cancel-after-write")]
    [InlineData(false, "invalid-handles")]
    [InlineData(false, "mutate-request")]
    [InlineData(false, "whole-file")]
    [InlineData(false, "restore")]
    [InlineData(false, "bad-modified")]
    public async Task ChangedFileStagesCanonicalDeltaAndRejectsStaleExpectedMappingsBeforeWrites(bool stale, string? batchMode)
    {
        var content = new TestContent();
        var owner = new SubjectIdentity("owner", "owner", "Owner");
        var originalBytes = Package("before");
        var updatedBytes = Package("after edit");
        var created = await IndexedDocumentPublicationBuilder.CreateAsync(Scope.ResourceId, "/file.docx", originalBytes,
            DocumentSecurity.Create(owner), content, new());
        var file = created.Partitions.Single(partition => partition.After.Kind == 0);
        var source = new SaveSource(file);
        var read = new SavePreparationRead(new(created.Head, DateTime.UtcNow, CoordinationState.Empty, [],
                created.Partitions.Select(partition => partition.After).ToImmutableArray()),
            new(created.Head, file.After, [], file.Checkpoint!.Bindings)
            {
                Mappings = file.Mappings.Where(mapping => mapping.Key.Kind == IndexedMappingKind.Manifest).ToImmutableArray()
                    .Add(file.Mappings.Single(mapping => mapping.Key.Kind == IndexedMappingKind.Cell) with
                    { Scope = file.EffectiveChanges.Scope with { LifecycleGeneration = 2 } }),
            }, null) { ReplayPosition = new(1, 0, 0) };
        ExGuid Fresh() => new(1, Guid.NewGuid());
        var graph = FileContentPartitionBuilder.BuildQueryChangesResponse(1, updatedBytes,
            new(Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(),
                IndexedDocumentGraph.Restore(created.Head.Package.Root.Cell), Guid.NewGuid()), file.After.Knowledge);
        var upload = graph.DataElementPackage!;
        var index = upload.DataElements.Single(element => element.DataElementType == DataElementType.StorageIndexDataElementData);
        var expectedIndex = Fresh();
        var expectedMappings = file.Mappings.Where(mapping => mapping.Key.Kind != IndexedMappingKind.Revision).Select(mapping =>
            new StorageIndexMapping(mapping.Key.Kind == IndexedMappingKind.Manifest ? StreamObjectTypeHeaderStart.StorageIndexManifestMapping :
                StreamObjectTypeHeaderStart.StorageIndexCellMapping, null,
                mapping.Key.Cell is { } cell ? IndexedDocumentGraph.Restore(cell) : null,
                stale && mapping.Key.Kind == IndexedMappingKind.Manifest ? Fresh() : IndexedDocumentGraph.Restore(mapping.Target),
                new(Guid.NewGuid(), mapping.Serial.Value)));
        upload.DataElements.Add(new(DataElementType.StorageIndexDataElementData, expectedIndex, SerialNumber.Null)
            { Data = StorageIndexPatch.Serialize(expectedMappings) });
        var request = new FsshttpbCellSubRequest(RequestTypes.PutChanges) { RequestId = 1,
            Data = new PutChangesSubRequestData { StorageIndex = index.DataElementExtendedGuid, ExpectedStorageIndex = expectedIndex, Flags = 1 } };
        // A retained physical element can be included again without another content write.
        var reused = file.AddedElements.First();
        upload.DataElements.Add(new((DataElementType)reused.Type, IndexedDocumentGraph.Restore(reused.Id),
            new(reused.Serial.Guid, reused.Serial.Value)) { Data = await content.ReadVerifiedAsync(reused.Payload, new StorageLimits().MaxObjectBytes) });
        var writes = content.Writes;
        using var cancellation = new CancellationTokenSource();
        content.BeforeBatch = () =>
        {
            if (batchMode == "failure") throw new IOException("Injected content batch failure.");
            if (batchMode == "cancel") cancellation.Cancel();
            if (batchMode == "mutate-request")
                foreach (var element in upload.DataElements) Array.Fill(element.Data!, (byte)0);
        };
        content.InvalidBatchHandles = batchMode == "invalid-handles";
        content.AfterBatch = () => { if (batchMode == "cancel-after-write") cancellation.Cancel(); };
        var writeContext = batchMode is "whole-file" or "restore" ? new DocumentWriteContext(
            new(1, batchMode == "whole-file" ? SaveOperationFamily.WholeFile : SaveOperationFamily.Restore, 0, "trusted-write"), new string('a', 64),
            batchMode == "whole-file" ? "host-lock" : null) : null;
        if (batchMode == "whole-file")
            read = read with { Control = read.Control with { Coordination = CoordinationState.Empty with
                { HostLock = new("host-lock", owner.Subject, read.Control.UtcNow.AddMinutes(1)) } } };
        var attributes = batchMode == "bad-modified" ? ImmutableDictionary<string, string>.Empty.Add("LastModifiedTime", "invalid")
            : ImmutableDictionary<string, string>.Empty;
        async Task<IndexedPreparedSave> Prepare() => await IndexedPartitionSavePreparer.PrepareAsync(read, request, upload,
            new(owner, Scope.ResourceId, 1, null, DocumentAccess.Read | DocumentAccess.Write)
                { AuthorityGeneration = created.Head.AuthorityGeneration, StateVersion = created.Head.StateVersion },
            attributes, source, content, new(), cancellation.Token, writeContext);
        if (batchMode is "failure" or "cancel" or "cancel-after-write" or "invalid-handles")
        {
            if (batchMode == "failure") await Assert.ThrowsAsync<IOException>(Prepare);
            else if (batchMode is "cancel" or "cancel-after-write") await Assert.ThrowsAnyAsync<OperationCanceledException>(Prepare);
            else await Assert.ThrowsAsync<StorageCorruptionException>(Prepare);
            Assert.Equal(1, content.Batches);
            Assert.Equal(writes + (batchMode == "cancel-after-write" ? content.BatchedObjects : 0), content.Writes);
            // No publication or receipt escaped staging, even if its physical objects now exist.
            return;
        }
        var result = await Prepare();
        if (batchMode == "bad-modified")
        {
            Assert.Equal("InvalidArgument", result.ErrorCode);
            Assert.Null(result.Publication);
            Assert.Equal(writes, content.Writes);
            Assert.Equal(0, content.Batches);
            return;
        }
        Assert.Null(result.ErrorCode);
        if (stale)
        {
            Assert.Null(result.Publication);
            Assert.True(Assert.Single(result.Response.SubResponses).Status);
            Assert.Equal(writes, content.Writes);
            Assert.Equal(0, content.Batches);
            return;
        }
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        var publication = Assert.IsType<PreparedDocumentPublication>(result.Publication);
        var part = Assert.Single(publication.Partitions,part => part.After.Kind == 0);
        Assert.Equal(3, part.MappingChanges.Length);
        Assert.DoesNotContain(part.AddedElements, element => element.Type == (uint)DataElementType.StorageIndexDataElementData);
        Assert.Equal(1, content.Batches);
        var responseObjects = writeContext is null ? 1 : 0;
        Assert.Equal(part.AddedElements.Length - 1 + responseObjects, content.BatchedObjects);
        Assert.Equal(writes + content.BatchedObjects, content.Writes);
        if (writeContext is null)
        {
            var handle = Assert.IsType<ContentHandle>(publication.Receipt.Response);
            Assert.Equal(result.Response.ToByteArray(), await content.ReadVerifiedAsync(handle));
            Assert.Contains(handle, publication.NewDocumentReferences);
        }
        else Assert.Null(publication.Receipt.Response);
        Assert.Equal(reused.Payload, part.AddedElements.Single(element => element.Id == reused.Id).Payload);
        Assert.DoesNotContain(source.RequestedKeys, key => key.Kind == IndexedMappingKind.Manifest);
        Assert.Contains(source.RequestedKeys, key => key.Kind == IndexedMappingKind.Cell);
        Assert.All(source.RequestedKeys.GroupBy(key => key), group => Assert.Single(group));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(updatedBytes)), publication.Package!.Sha256);
        Assert.Contains(part.EffectiveChanges.Changes, change => change.Value is null);
        Assert.Contains(part.EffectiveChanges.Changes, change => change.Value is not null);
        Assert.DoesNotContain(part.MappingChanges, change => change.Key.Kind == IndexedMappingKind.Revision &&
            file.Mappings.Any(old => old.Key == change.Key));
        var bindings = IndexedDocumentGraph.Replay(file.Checkpoint!, [part.EffectiveChanges], part.After.PublicationSequence, new());
        var descriptors = file.AddedElements.Concat(part.AddedElements).DistinctBy(element => element.Id).ToDictionary(element => element.Id);
        var reader = new GraphDocumentPackageReader((_, _) => ValueTask.FromResult<IReadOnlyList<IndexedObjectBinding>>(bindings),
            async (_, id, ct) =>
            {
                var descriptor = descriptors[id];
                return new((DataElementType)descriptor.Type, IndexedDocumentGraph.Restore(id), new(descriptor.Serial.Guid, descriptor.Serial.Value))
                    { Data = await content.ReadVerifiedAsync(descriptor.Payload, new StorageLimits().MaxObjectBytes, ct) };
            }, new StorageLimits().MaxDocumentBytes);
        await using var materialized = await reader.OpenReadAsync(publication.Package);
        using var bytes = new MemoryStream();
        await materialized.CopyToAsync(bytes);
        Assert.Equal(updatedBytes, bytes.ToArray());

        static byte[] Package(string text)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var name in new[] { "[Content_Types].xml", "word/document.xml" })
                {
                    using var entry = new StreamWriter(zip.CreateEntry(name).Open());
                    entry.Write("<root>" + text + "</root>");
                }
            return buffer.ToArray();
        }
    }

    [Fact]
    public async Task MetadataPatchRetainsUntouchedCellsAndInheritedObjectPartitions()
    {
        var content = new TestContent();
        var owner = new SubjectIdentity("owner", "owner", "Owner");
        var created = await IndexedDocumentPublicationBuilder.CreateAsync(Scope.ResourceId, "/file.docx", [1, 2, 3],
            DocumentSecurity.Create(owner), content, new());
        var empty = created.Partitions.Single(partition => partition.After.Kind == 1);
        var fixture = new GraphFixture([9, 8, 7], blob: true);
        var upload = new DataElementPackage();
        upload.DataElements.AddRange(fixture.Elements);
        var originalRequest = new FsshttpbCellSubRequest(RequestTypes.PutChanges)
            { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = fixture.Index, AdditionalFlagsBits = 1 } };
        var original = await Prepare(created.Head, empty, empty.Checkpoint!.Bindings, originalRequest, upload);
        var first = Assert.IsType<PreparedDocumentPublication>(original.Publication);
        var baseline = Assert.Single(first.Partitions);
        Assert.NotEqual(IndexedDocumentGraph.Capture(fixture.Index), baseline.After.StorageIndex);
        Assert.Equal(baseline.After.StorageIndex, first.Receipt.AcceptedStorageIndex);
        Assert.Equal(created.Head.ContentVersion, first.Receipt.ContentVersion);
        var currentBindings = IndexedDocumentGraph.Replay(empty.Checkpoint!, [baseline.EffectiveChanges], baseline.After.PublicationSequence, new());
        var currentHead = created.Head with { LatestRevisionNumber = first.Receipt.RevisionNumber, Package = first.Package! };
        var patch = fixture.CreatePatch(fixture.Elements.Single(element => element.DataElementExtendedGuid.Equals(fixture.Index)), other: false);
        var changed = await Prepare(currentHead, baseline, currentBindings, patch.SubRequests.Single(), patch.DataElementPackage!);
        Assert.False(Assert.Single(changed.Response.SubResponses).Status);
        var second = Assert.IsType<PreparedDocumentPublication>(changed.Publication);
        var partition = Assert.Single(second.Partitions);
        var otherCell = IndexedDocumentGraph.Capture(fixture.OtherCell);
        Assert.DoesNotContain(partition.EffectiveChanges.Changes, change => change.Key.Cell == otherCell);
        Assert.Equal(2, partition.MappingChanges.Length);
        Assert.DoesNotContain(partition.MappingChanges, change => change.Key.Kind == IndexedMappingKind.Manifest);
        Assert.Equal(created.Head.Package.Sha256, second.Package!.Sha256);
        Assert.Equal(created.Head.ContentVersion, second.Receipt.ContentVersion);
        var replayed = IndexedDocumentGraph.Replay(IndexedDocumentGraph.Checkpoint(Scope with { TargetPartition = 1 },
            baseline.After.PublicationSequence, currentBindings), [partition.EffectiveChanges], partition.After.PublicationSequence, new());
        Assert.Contains(replayed, binding => binding.Key.ObjectPartitionId == 4);
        Assert.Contains(replayed, binding => binding.Key.ObjectId == Id(31) && binding.GroupElement == Id(7));

        ValueTask<IndexedPreparedSave> Prepare(DocumentHead head, PreparedPartitionPublication previous,
            ImmutableArray<IndexedObjectBinding> bindings, FsshttpbCellSubRequest request, DataElementPackage incoming)
        {
            var control = new DocumentControlRead(head, DateTime.UtcNow, CoordinationState.Empty, [],
                created.Partitions.Select(partition => partition.After.Kind == 1 ? previous.After : partition.After).ToImmutableArray());
            var read = new SavePreparationRead(control, new(head, previous.After, [], bindings), null)
                { ReplayPosition = new(previous.After.PublicationSequence, 0, 0) };
            return IndexedPartitionSavePreparer.PrepareAsync(read, request, incoming,
                new(owner, Scope.ResourceId, 1, null, DocumentAccess.Read | DocumentAccess.Write)
                    { AuthorityGeneration = head.AuthorityGeneration, StateVersion = head.StateVersion },
                ImmutableDictionary<string, string>.Empty, new SaveSource(previous), content, new());
        }
    }

    [Fact]
    public async Task ScopedClosureMatchesExistingGenericGraphWithoutReadingGroupOrBlobPayloads()
    {
        var fixture = new GraphFixture([1, 2, 3], blob: true);
        var source = new Source(fixture);
        var selected = await IndexedDocumentGraph.ResolveCellAsync(Scope, Id(1),
            IndexedDocumentGraph.Capture(fixture.Cell), Id(20), source);
        var loaded = new List<DataElementType>();
        var actual = await IndexedDocumentGraph.RequiredElementsAsync(Scope, Id(1), selected, source, Read);
        var expected = fixture.Generic().GetRequiredElements(fixture.Cell).Select(IndexedDocumentGraph.Capture);
        Assert.Equal(expected.OrderBy(id => id.Value), actual.OrderBy(id => id.Value));
        Assert.All(loaded, kind => Assert.Equal(DataElementType.CellManifestDataElementData, kind));
        ValueTask<DataElement> Read(IndexedGraphScope scope, ExtendedId id, CancellationToken ct)
        {
            var element = fixture.Elements.Single(value => IndexedDocumentGraph.Capture(value.DataElementExtendedGuid) == id);
            loaded.Add(element.DataElementType);
            return ValueTask.FromResult(element);
        }
    }

    private sealed class SaveSource(PreparedPartitionPublication file) : IIndexedSaveSource
    {
        public List<ExtendedId> RequestedIndexes { get; } = [];
        public List<IndexedMappingKey> RequestedKeys { get; } = [];
        public List<(IndexedGraphScope Scope, ExtendedId Id)> RequestedGroups { get; } = [];
        public List<(IndexedGraphScope Scope, ExtendedId Id)> RequestedRevisions { get; } = [];
        public ValueTask<ImmutableArray<GraphElementState>> ReadElementsAsync(IndexedGraphScope scope,
            IReadOnlyCollection<ExtendedId> ids, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(file.AddedElements.Where(element => ids.Contains(element.Id)).ToImmutableArray());
        public ValueTask<ImmutableArray<ExtendedId>> ReadVisibleElementIdsAsync(IndexedGraphScope scope,long publicationSequence,
            IReadOnlyCollection<ExtendedId> elementIds,CancellationToken cancellationToken=default) =>
            ValueTask.FromResult(file.EffectiveChanges.Scope == scope && file.After.PublicationSequence == publicationSequence
                ? file.AddedElements.Where(element => elementIds.Contains(element.Id)).Select(element => element.Id).ToImmutableArray() : []);
        public ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingClaimsAsync(IndexedGraphScope scope,
            IReadOnlyCollection<SerialId> serials, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(file.Mappings.Where(mapping => serials.Contains(mapping.Serial)).ToImmutableArray());
        public ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingsAsync(IndexedGraphScope scope,
            long publicationSequence, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Routine preparation must not enumerate the complete mapping table.");
        public ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex,
            IndexedMappingKey key, CancellationToken cancellationToken = default)
        {
            RequestedIndexes.Add(storageIndex);
            RequestedKeys.Add(key);
            var value = file.MappingChanges.SingleOrDefault(change => change.Key == key)?.Value;
            return ValueTask.FromResult<IndexedStorageMapping?>(file.EffectiveChanges.Scope == scope && file.After.StorageIndex == storageIndex && value is not null
                ? new(scope, storageIndex, key, value.Target, value.Serial) : null);
        }
        public ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest,
            CancellationToken cancellationToken = default)
        {
            RequestedRevisions.Add((scope, manifest));
            return ValueTask.FromResult(file.Revisions.SingleOrDefault(revision => revision.Scope == scope && revision.ElementId == manifest));
        }
        public ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group,
            CancellationToken cancellationToken = default)
        {
            RequestedGroups.Add((scope, group));
            return ValueTask.FromResult(file.Objects.Where(obj => obj.Scope == scope && obj.GroupElement == group).ToImmutableArray());
        }
    }

    private sealed class TestContent : IContentStore, IContentBatchWriter
    {
        private readonly Dictionary<string, byte[]> values = [];
        public int Writes { get; private set; }
        public int Batches { get; private set; }
        public int BatchedObjects { get; private set; }
        public Action? BeforeBatch { get; set; }
        public Action? AfterBatch { get; set; }
        public bool InvalidBatchHandles { get; set; }
        public async ValueTask<IReadOnlyList<ContentHandle>> WriteManyAsync(IReadOnlyList<ReadOnlyMemory<byte>> contents,
            CancellationToken cancellationToken = default)
        {
            Batches++;
            BeforeBatch?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (InvalidBatchHandles) return [];
            BatchedObjects += contents.Count;
            var result = new List<ContentHandle>();
            foreach (var content in contents)
            {
                using var input = new MemoryStream(content.ToArray(), writable: false);
                result.Add(await WriteAsync(input, cancellationToken));
            }
            AfterBatch?.Invoke();
            return result;
        }
        public bool Durable => false;
        public bool Shared => false;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            values[hash] = bytes;
            Writes++;
            return new(hash, bytes.Length, hash);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(values[handle.Key], writable: false));
    }

    private sealed class Source : IIndexedGraphSource
    {
        public List<IndexedStorageMapping> Mappings { get; } = [];
        private readonly Dictionary<ExtendedId, IndexedRevisionManifest> revisions = [];
        private readonly Dictionary<ExtendedId, ImmutableArray<IndexedObjectDefinition>> groups = [];
        public Source(GraphFixture fixture)
        {
            foreach (var element in fixture.Elements) Add(element);
        }
        public void Add(DataElement element)
        {
            var metadata = IndexedDocumentGraph.ReadMetadata(Scope, element);
            Mappings.AddRange(metadata.Mappings);
            if (metadata.Revision is { } revision) revisions[revision.ElementId] = revision;
            if (element.DataElementType == DataElementType.ObjectGroupDataElementData)
                groups[IndexedDocumentGraph.Capture(element.DataElementExtendedGuid)] = metadata.Objects;
        }
        public ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex,
            IndexedMappingKey key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Mappings.SingleOrDefault(mapping => mapping.Scope == scope && mapping.StorageIndex == storageIndex && mapping.Key == key));
        public ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(revisions.GetValueOrDefault(manifest));
        public ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(groups.GetValueOrDefault(group));
    }
}
