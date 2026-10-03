namespace CellBridge.FssHttpB.Tests;

public class RetentionAndKnowledgeTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(3UL)]
    [InlineData(4UL)]
    [InlineData(ulong.MaxValue)]
    public void ChangeFrequencyMetadataIsTypedAndDoesNotBlockCompaction(ulong frequency)
    {
        var identity = Identity(Guid.NewGuid());
        var elements = FileContentPartitionBuilder.BuildQueryChangesResponse(1, [1, 2], identity, 1).DataElementPackage!.DataElements;
        var group = elements.Single(e => e.DataElementType == DataElementType.ObjectGroupDataElementData);
        var reader = new BinaryReaderEx(group.Data!);
        _ = StreamObjectHeaderStart.Parse(reader);
        while ((group.Data![reader.Position] & 3) is 0 or 2)
        { var header = StreamObjectHeaderStart.Parse(reader); reader.Skip(header.Length); }
        _ = StreamObjectHeaderEnd.Parse(reader);
        int offset = reader.Position;
        var body = new BinaryWriterEx();
        new Compact64bitInt(frequency).Serialize(body);
        var metadata = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupMetadataDeclarations, 0).Serialize(metadata);
        new StreamObjectHeaderStart32Bit((StreamObjectTypeHeaderStart)0x78, body.Length).Serialize(metadata);
        metadata.WriteBytes(body.ToArray());
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.ObjectGroupMetadataDeclarations).Serialize(metadata);
        var original = group.Data!;
        group.Data = original[..offset].Concat(metadata.ToArray()).Concat(original[offset..]).ToArray();
        Assert.Equal(frequency, Assert.Single(ObjectGroupDataElement.Parse(group).ChangeFrequencies));
        Assert.True(PartitionGraphSnapshot.Create(elements, identity.ObjectDataBlobGuid).AnalyzeRetention().Complete);
        // Expand the record by one byte without changing its Compact64bitInt.
        var malformed = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupMetadataDeclarations, 0).Serialize(malformed);
        new StreamObjectHeaderStart32Bit((StreamObjectTypeHeaderStart)0x78, body.Length + 1).Serialize(malformed);
        malformed.WriteBytes(body.ToArray());
        malformed.WriteByte(0);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.ObjectGroupMetadataDeclarations).Serialize(malformed);
        group.Data = original[..offset].Concat(malformed.ToArray()).Concat(original[offset..]).ToArray();
        Assert.Throws<InvalidDataException>(() => ObjectGroupDataElement.Parse(group));
    }

    [Fact]
    public void UnrelatedMappedRevisionCannotResolveRootInCurrentRevision()
    {
        var current = Identity(Guid.NewGuid());
        var unrelated = Identity(Guid.NewGuid());
        var elements = FileContentPartitionBuilder.BuildQueryChangesResponse(1, [1, 2], current, 1).DataElementPackage!.DataElements;
        var extra = FileContentPartitionBuilder.BuildQueryChangesResponse(1, [3, 4], unrelated, 1).DataElementPackage!.DataElements;
        elements.AddRange(extra.Where(e => e.DataElementType is DataElementType.RevisionManifestDataElementData or DataElementType.ObjectGroupDataElementData));
        var index = elements.Single(e => e.DataElementType == DataElementType.StorageIndexDataElementData);
        var mapping = new BinaryWriterEx();
        unrelated.RevisionId.Serialize(mapping);
        unrelated.RevisionManifestGuid.Serialize(mapping);
        new SerialNumber(unrelated.SerialGuid, 55).Serialize(mapping);
        var appendix = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, mapping.Length).Serialize(appendix);
        appendix.WriteBytes(mapping.ToArray());
        index.Data = index.Data!.Concat(appendix.ToArray()).ToArray();
        var revision = elements.Single(e => e.DataElementExtendedGuid.Equals(current.RevisionManifestGuid));
        var root = new BinaryWriterEx();
        new ExGuid(3, StorageManifestBuilder.RootExtendedGuid).Serialize(root);
        unrelated.ObjectGuid.Serialize(root);
        var declaration = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.RevisionManifestRootDeclare, root.Length).Serialize(declaration);
        declaration.WriteBytes(root.ToArray());
        revision.Data = revision.Data!.Concat(declaration.ToArray()).ToArray();
        var graph = PartitionGraphSnapshot.Create(elements, current.ObjectDataBlobGuid);
        Assert.Equal(new byte[] { 1, 2 }, graph.Materialize());
        Assert.Contains(graph.AnalyzeRetention().Blockers, s => s.Contains("Unresolved declared root"));
        Assert.Throws<InvalidDataException>(() => graph.Compact());
    }

    [Fact]
    public void MappingSerialsAreIndependentOfTargetElementSerials()
    {
        var identity = Identity(Guid.NewGuid());
        var elements = FileContentPartitionBuilder.BuildQueryChangesResponse(1, [1, 2], identity, 1).DataElementPackage!.DataElements;
        foreach (var e in elements) e.SerialNumber = new(Guid.NewGuid(), 100);
        var graph = PartitionGraphSnapshot.Create(elements, identity.ObjectDataBlobGuid);
        Assert.Equal(new byte[] { 1, 2 }, graph.Materialize());
        var knowledge = ClientKnowledge.FromElements(elements);
        Assert.All(graph.MappingSerials.Values.SelectMany(s => s), s => Assert.True(knowledge.Contains(s)));
    }

    [Fact]
    public void MappingKeyMustMatchItsRevisionManifestEvenWhenAnotherKeyMatches()
    {
        var identity = Identity(Guid.NewGuid());
        var elements = FileContentPartitionBuilder.BuildQueryChangesResponse(1, [1, 2], identity, 1).DataElementPackage!.DataElements;
        var index = elements.Single(e => e.DataElementType == DataElementType.StorageIndexDataElementData);
        var body = new BinaryWriterEx();
        new ExGuid(99, Guid.NewGuid()).Serialize(body);
        identity.RevisionManifestGuid.Serialize(body);
        new SerialNumber(Guid.NewGuid(), 1).Serialize(body);
        var mapping = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, body.Length).Serialize(mapping);
        mapping.WriteBytes(body.ToArray());
        index.Data = index.Data!.Concat(mapping.ToArray()).ToArray();
        var graph = PartitionGraphSnapshot.Create(elements, identity.ObjectDataBlobGuid);
        Assert.Equal(new byte[] { 1, 2 }, graph.Materialize());
        Assert.False(graph.AnalyzeRetention().Complete);
        Assert.Throws<InvalidDataException>(() => graph.Compact());
    }

    internal static StorageManifestBuilder.StableIdentity Identity(Guid guid) => new(
        new(1, guid), new(2, guid), new(3, guid), new(4, guid), new(5, guid), new(6, guid), new(7, guid),
        new(new(8, guid), new(9, StorageManifestBuilder.CellSecondExtendedGuid)), guid);

    [Fact]
    public void CompactionKeepsReferencesAndLeavesPreviousSnapshotUnchanged()
    {
        var firstId = Identity(Guid.NewGuid());
        var firstElements = FileContentPartitionBuilder.BuildQueryChangesResponse(1, [1, 2], firstId, 1).DataElementPackage!.DataElements;
        var before = PartitionGraphSnapshot.Create(firstElements, firstId.ObjectDataBlobGuid);
        var nextId = Identity(Guid.NewGuid());
        var nextElements = FileContentPartitionBuilder.BuildQueryChangesResponse(2, [3, 4], nextId, 2).DataElementPackage!.DataElements;
        var merged = before.Merge(nextElements, nextId.ObjectDataBlobGuid);
        var report = merged.AnalyzeRetention();
        Assert.True(report.Complete, string.Join(";", report.Blockers));
        Assert.Equal(5, report.RequiredElements);
        var compact = merged.Compact();
        Assert.Equal(5, compact.ElementCount);
        Assert.Equal(new byte[] { 3, 4 }, compact.Materialize());
        Assert.Equal(new byte[] { 1, 2 }, before.Materialize());
        Assert.Equal(10, merged.ElementCount);
        Assert.Equal(10, merged.Compact([firstId.ObjectDataBlobGuid]).ElementCount);
        Assert.Equal(compact.PayloadBytes, compact.Compact().PayloadBytes);
    }

    [Fact]
    public void BaseRevisionMissingFromMappingsPreventsPruningEvenWhenFileMaterializes()
    {
        var identity = Identity(Guid.NewGuid());
        var elements = FileContentPartitionBuilder.BuildQueryChangesResponse(1, [1, 2], identity, 1).DataElementPackage!.DataElements;
        var revision = elements.Single(e => e.DataElementType == DataElementType.RevisionManifestDataElementData);
        elements[elements.IndexOf(revision)] = StorageManifestBuilder.BuildRevisionManifestDataElement(
            identity.RevisionManifestGuid, revision.SerialNumber, identity.RevisionId, new(7, Guid.NewGuid()),
            new(2, StorageManifestBuilder.RootExtendedGuid), identity.ObjectGuid, identity.ObjectGroupGuid);
        var graph = PartitionGraphSnapshot.Create(elements, identity.ObjectDataBlobGuid);
        Assert.Equal(new byte[] { 1, 2 }, graph.Materialize());
        Assert.Contains(graph.AnalyzeRetention().Blockers, s => s.Contains("revision"));
        Assert.Throws<InvalidDataException>(() => graph.Compact());
    }

    [Fact]
    public void KnowledgeMatchesSerialGuidAndExactRangesAndRoundTripsInRequests()
    {
        var guid = Guid.NewGuid();
        var elements = new[] { 1UL, 3UL, ulong.MaxValue }.Select(n => new DataElement(
            DataElementType.ObjectGroupDataElementData, new(1, Guid.NewGuid()), new(guid, n))).ToArray();
        var knowledge = ClientKnowledge.FromElements(elements);
        Assert.True(knowledge.Contains(new(guid, ulong.MaxValue)));
        Assert.False(knowledge.Contains(new(guid, 2)));
        Assert.False(knowledge.Contains(new(Guid.NewGuid(), 3)));
        Assert.False(knowledge.Contains(SerialNumber.Null));
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { Data = new QueryChangesSubRequestData
        { Knowledge = knowledge, IncludeStorageManifest = true, IncludeCellChanges = true,
            IncludeFilteredOutDataElementsInKnowledge = true, RoundKnowledgeToWholeCellChanges = true } });
        var reader = new BinaryReaderEx(request.ToByteArray());
        var decoded = FsshttpbCellRequest.Deserialize(reader);
        var query = Assert.IsType<QueryChangesSubRequestData>(Assert.Single(decoded.SubRequests).Data);
        Assert.Equal(0, reader.Remaining);
        Assert.True(query.Knowledge!.Contains(new(guid, 3)));
        Assert.True(query.IncludeFilteredOutDataElementsInKnowledge);
        Assert.True(query.RoundKnowledgeToWholeCellChanges);
        Assert.Throws<InvalidDataException>(() => ClientKnowledge.Deserialize(
            new BinaryReaderEx(BinaryKnowledgeBuilder.FromElements(elements, ExGuid.Null, 0)), maxEntries: 1));
    }

    [Theory]
    [InlineData(9_999, false)]
    [InlineData(10_000, true)]
    public void EchoedServerKnowledgeFallsBackCompletelyAboveTheDecodedEntryLimit(int rangeCount, bool fallback)
    {
        var guid = Guid.NewGuid();
        var serials = Enumerable.Range(0, rangeCount).Select(i => new SerialNumber(guid, (ulong)i * 2 + 1));
        var wire = BinaryKnowledgeBuilder.FromElements([], ExGuid.Null, 0, mappingSerials: serials);
        var reader = new BinaryReaderEx(wire);
        var knowledge = ClientKnowledge.Deserialize(reader);
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(fallback, knowledge.RangeLimitExceeded); // Includes the builder's waterline entry.
        Assert.Equal(fallback, knowledge.RequiresFullResponse);
        Assert.Equal(!fallback, knowledge.Contains(new(guid, 1)));
        Assert.Equal(fallback ? 0 : rangeCount, knowledge.Ranges.Count);
        var encoded = new BinaryWriterEx();
        knowledge.Serialize(encoded);
        Assert.Equal(wire, encoded.ToArray());
        if (fallback)
            Assert.Throws<InvalidDataException>(() => ClientKnowledge.Deserialize(new(wire), maxEntries: 10_000));
        var malformed = wire.ToArray();
        malformed[^1] = 0x05; // Wrong closing object even after fallback.
        Assert.Throws<InvalidDataException>(() => ClientKnowledge.Deserialize(new(malformed)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryCellMappingMustTargetACellManifestBeforeCompaction(bool targetIndex)
    {
        var identity = Identity(Guid.NewGuid());
        var elements = FileContentPartitionBuilder.BuildQueryChangesResponse(1, [1, 2], identity, 1).DataElementPackage!.DataElements;
        var index = elements.Single(e => e.DataElementType == DataElementType.StorageIndexDataElementData);
        var body = new BinaryWriterEx();
        new CellId(new(1, Guid.NewGuid()), new(2, Guid.NewGuid())).Serialize(body);
        (targetIndex ? identity.ObjectDataBlobGuid : identity.ObjectGroupGuid).Serialize(body);
        new SerialNumber(identity.SerialGuid, 55).Serialize(body);
        var extra = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.StorageIndexCellMapping, body.Length).Serialize(extra);
        extra.WriteBytes(body.ToArray());
        index.Data = [.. index.Data!, .. extra.ToArray()];
        var graph = PartitionGraphSnapshot.Create(elements, identity.ObjectDataBlobGuid);
        Assert.Equal(new byte[] { 1, 2 }, graph.Materialize());
        Assert.False(graph.AnalyzeRetention().Complete);
        Assert.Throws<InvalidDataException>(() => graph.Compact());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(34, false)]
    public void OversizedKnowledgeStillValidatesUnknownSpecializationEndsAndNesting(int depth, bool wrongEnd)
    {
        var guid = Guid.NewGuid();
        var serials = Enumerable.Range(0, 10_001).Select(i => new SerialNumber(guid, (ulong)i * 2 + 1));
        var wire = BinaryKnowledgeBuilder.FromElements([], ExGuid.Null, 0, mappingSerials: serials);
        var extra = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.SpecializedKnowledge, 16).Serialize(extra);
        extra.WriteBytes(Guid.NewGuid().ToByteArray());
        for (int i = 0; i < depth; i++)
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.CellKnowledge, 0).Serialize(extra);
        for (int i = 0; i < depth; i++)
            new StreamObjectHeaderEnd8Bit(wrongEnd ? StreamObjectTypeHeaderEnd.Knowledge : StreamObjectTypeHeaderEnd.CellKnowledge).Serialize(extra);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SpecializedKnowledge).Serialize(extra);
        byte[] bytes = [.. wire[..^1], .. extra.ToArray(), wire[^1]];
        if (wrongEnd || depth > 32)
            Assert.Throws<InvalidDataException>(() => ClientKnowledge.Deserialize(new(bytes)));
        else
        {
            var knowledge = ClientKnowledge.Deserialize(new(bytes));
            Assert.True(knowledge.RangeLimitExceeded);
            Assert.True(knowledge.HasUnsupportedSpecialization);
            Assert.True(knowledge.RequiresFullResponse);
            Assert.Empty(knowledge.Ranges);
        }
    }

    [Fact]
    public void RangeFallbackDoesNotAllowUnboundedSpecializationTracking()
    {
        var wire = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.Knowledge, 0).Serialize(wire);
        for (int i = 0; i < 10_001; i++)
        {
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.SpecializedKnowledge, 16).Serialize(wire);
            wire.WriteBytes(Guid.NewGuid().ToByteArray());
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.CellKnowledge, 0).Serialize(wire);
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.CellKnowledge).Serialize(wire);
            new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SpecializedKnowledge).Serialize(wire);
        }
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.Knowledge).Serialize(wire);
        var error = Assert.Throws<InvalidDataException>(() => ClientKnowledge.Deserialize(new(wire.ToArray())));
        Assert.Contains("specialization limit", error.Message);
    }

    [Fact]
    public void ReaderBoundsDoNotPermitNegativeOrOverflowingReadsOrEscapingSlice()
    {
        var reader = new BinaryReaderEx(new byte[] { 1, 2, 3 }, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Position = 0);
        Assert.Throws<EndOfStreamException>(() => reader.ReadBytes(-1));
        Assert.Throws<EndOfStreamException>(() => reader.Skip(int.MaxValue));
        Assert.Equal(2, reader.ReadByte());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedFiltersFollowFailIfUnsupportedFlag(bool fail)
    {
        var wire = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesRequest, 1).Serialize(wire);
        wire.WriteByte(0);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesFilter, 2).Serialize(wire);
        wire.WriteBytes(new byte[] { 5, 1 }); // Custom filter, include.
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.QueryChangesFilter).Serialize(wire);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesFilterFlags, 1).Serialize(wire);
        wire.WriteByte(fail ? (byte)1 : (byte)0);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubRequest).Serialize(wire);
        var query = QueryChangesSubRequestData.Deserialize(new(wire.ToArray()));
        Assert.Equal(fail, query.HasUnsupportedQueryControls);
    }

    [Fact]
    public void CellScopeRoundTripsWithoutEmittingAnUnframedFilter()
    {
        var cell = Identity(Guid.NewGuid()).CellId;
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { Data = new QueryChangesSubRequestData
        { IncludeCellChanges = true, CellId = cell } });
        var decoded = FsshttpbCellRequest.Deserialize(new(request.ToByteArray()));
        var query = Assert.IsType<QueryChangesSubRequestData>(decoded.SubRequests[0].Data);
        Assert.Equal(cell, query.CellId);
        Assert.False(query.HasUnsupportedQueryControls);
    }
}
