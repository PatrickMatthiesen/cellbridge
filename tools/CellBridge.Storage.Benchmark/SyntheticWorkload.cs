using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;

internal static class SyntheticWorkload
{
    public static FsshttpbCellRequest Save(StoredDocument current, byte[] payload, string mode, int iteration, Random random)
    {
        byte[] guidBytes = new byte[16]; random.NextBytes(guidBytes);
        var id = new Guid(guidBytes);
        var cell = current.FilePartition.ProtocolIdentity.CellId;
        var identity = new StorageManifestBuilder.StableIdentity(new(1, id), new(2, id), new(3, id),
            new(4, id), new(5, id), new(6, id), new(7, id), cell, id);
        var graph = current.FilePartition.FileGraph;
        var generated = FileContentPartitionBuilder.BuildQueryChangesResponse(1,
            mode.StartsWith("delta", StringComparison.Ordinal) ? [] : payload, identity, (ulong)iteration + 1);
        var package = generated.DataElementPackage!;
        if (mode.StartsWith("delta", StringComparison.Ordinal))
        {
            package.DataElements.RemoveAll(e => e.DataElementType == DataElementType.ObjectGroupDataElementData);
            var oldRevision = graph.Elements.Single(e => e.DataElementType == DataElementType.RevisionManifestDataElementData &&
                RevisionId(e).Equals(graph.Revision));
            var groups = ObjectGroups(oldRevision);
            var revision = package.DataElements.Single(e => e.DataElementType == DataElementType.RevisionManifestDataElementData);
            package.DataElements[package.DataElements.IndexOf(revision)] = StorageManifestBuilder.BuildRevisionManifestDataElement(
                identity.RevisionManifestGuid, revision.SerialNumber, identity.RevisionId,
                mode == "delta-chain" ? graph.Revision! : ExGuid.Null,
                new(2, StorageManifestBuilder.RootExtendedGuid), graph.RootObject!, groups);
            if (mode == "delta-chain")
            {
                var proposed = package.DataElements.Single(e => e.DataElementType == DataElementType.StorageIndexDataElementData);
                var previous = graph.StorageIndexes.Single(e => e.DataElementExtendedGuid.Equals(graph.StorageIndex));
                var mappings = new BinaryWriterEx();
                var reader = new BinaryReaderEx(previous.Data!);
                while (reader.Remaining > 0)
                {
                    int start = reader.Position;
                    var header = StreamObjectHeaderStart.Parse(reader);
                    reader.Skip(header.Length);
                    int end = reader.Position;
                    if (header.Type == StreamObjectTypeHeaderStart.StorageIndexRevisionMapping)
                    { reader.Position = start; mappings.WriteBytes(reader.ReadBytes(end - start)); }
                }
                proposed.Data = [..proposed.Data!, ..mappings.ToArray()];
            }
        }
        var request = new FsshttpbCellRequest { DataElementPackage = package };
        request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = (ulong)iteration + 1,
            Data = new PutChangesSubRequestData { StorageIndex = identity.ObjectDataBlobGuid, ExpectedStorageIndex = graph.StorageIndex } });
        return request;
    }

    private static ExGuid RevisionId(DataElement element)
    {
        var reader = new BinaryReaderEx(element.Data!);
        _ = StreamObjectHeaderStart.Parse(reader);
        return ExGuid.Deserialize(reader);
    }

    private static List<ExGuid> ObjectGroups(DataElement element)
    {
        var reader = new BinaryReaderEx(element.Data!);
        var start = StreamObjectHeaderStart.Parse(reader);
        reader.Skip(start.Length);
        var result = new List<ExGuid>();
        while (reader.Remaining > 0)
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            var body = new BinaryReaderEx(reader.ReadMemory(header.Length));
            if (header.Type == StreamObjectTypeHeaderStart.RevisionManifestObjectGroupReferences)
                result.Add(ExGuid.Deserialize(body));
        }
        return result;
    }

    public static FsshttpbCellRequest Capture(string root, string name)
    {
        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, name + ".json")));
        var side = fixture.RootElement.GetProperty("request");
        var parts = MtomMessageParser.ParseViews(Convert.FromBase64String(side.GetProperty("bodyBase64").GetString()!),
            side.GetProperty("contentType").GetString()!);
        var binary = parts.Single(p => p.ContentType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase));
        return FsshttpbCellRequest.Deserialize(new BinaryReaderEx(binary.ContentMemory));
    }
}
