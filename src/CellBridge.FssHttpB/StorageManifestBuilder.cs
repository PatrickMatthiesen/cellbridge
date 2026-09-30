namespace CellBridge.FssHttpB;

/// <summary>
/// Builds the data elements needed for a Query Changes response so that a
/// protocol client (Word desktop) can download a file's content.
///
/// The wire layouts follow the reference implementation in
/// Interop-TestSuites (SharedAdapter/Stack/FSSHTTPB/BasicType/DataPackage)
/// and the [MS-FSSHTTPB] v20240820 spec sections 2.2.1.12.3 through
/// 2.2.1.12.8 and 2.2.3.1.2.
///
/// A complete file download is described by a small graph of data elements:
/// <list type="bullet">
/// <item>Storage Manifest (root) — declares the schema and the root cell.</item>
/// <item>Cell Manifest — points at the current revision of the cell.</item>
/// <item>Revision Manifest — links the revision to its object group.</item>
/// <item>Object Group — declares the object and carries its raw bytes.</item>
/// <item>Storage Index — identifies the returned cell storage.</item>
/// </list>
/// </summary>
public static class StorageManifestBuilder
{
    /// <summary>
    /// Builds the compact StorageIndex data element used by SharePoint's
    /// 13/11 knowledge-sensitive QueryChanges responses.  A response that
    /// already has the requested partition in its knowledge contains this
    /// element only; it does not repeat the manifest or object data graph.
    /// </summary>
    /// <param name="dataElementGuid">The storage-index data-element ID.</param>
    /// <param name="serialNumber">The data-element serial number.</param>
    /// <param name="emitNullManifestMapping">When true, emits the two-byte
    /// null manifest mapping used by the captured Metadata response.  When
    /// false, emits the empty mapping used by the captured FileContents
    /// response.</param>
    public static DataElement BuildStorageIndexDataElement(
        ExGuid dataElementGuid,
        SerialNumber serialNumber,
        bool emitNullManifestMapping = true)
    {
        ArgumentNullException.ThrowIfNull(dataElementGuid);
        ArgumentNullException.ThrowIfNull(serialNumber);

        var payload = new BinaryWriterEx();
        var mapping = new BinaryWriterEx();
        if (emitNullManifestMapping)
        {
            ExGuid.Null.Serialize(mapping);
            SerialNumber.Null.Serialize(mapping);
        }

        // A FileContents StorageIndexDataElementData has no manifest mapping
        // object at all.  SharePoint's captured 13/11 response starts the
        // data payload with the next mapping/end marker; emitting a zero
        // length StorageIndexManifestMapping header (even with an empty
        // payload) shifts the remainder of the response and is rejected by
        // Word.  Metadata responses use the explicit null mapping.
        if (emitNullManifestMapping)
        {
            new StreamObjectHeaderStart16Bit(
                StreamObjectTypeHeaderStart.StorageIndexManifestMapping,
                mapping.Length).Serialize(payload);
            payload.WriteBytes(mapping.ToArray());
        }

        return new DataElement(
            DataElementType.StorageIndexDataElementData,
            dataElementGuid,
            serialNumber)
        {
            Data = payload.ToArray(),
        };
    }

    /// <summary>
    /// Builds a StorageIndex that resolves the manifest, cell, and revision
    /// elements in one QueryChanges graph.
    /// </summary>
    public static DataElement BuildStorageIndexDataElement(
        ExGuid dataElementGuid,
        SerialNumber serialNumber,
        ExGuid manifestMapping,
        SerialNumber manifestMappingSerial,
        CellId cellId,
        ExGuid cellMapping,
        SerialNumber cellMappingSerial,
        ExGuid revision,
        ExGuid revisionMapping,
        SerialNumber revisionMappingSerial)
    {
        ArgumentNullException.ThrowIfNull(dataElementGuid);
        ArgumentNullException.ThrowIfNull(serialNumber);
        ArgumentNullException.ThrowIfNull(manifestMapping);
        ArgumentNullException.ThrowIfNull(manifestMappingSerial);
        ArgumentNullException.ThrowIfNull(cellId);
        ArgumentNullException.ThrowIfNull(cellMapping);
        ArgumentNullException.ThrowIfNull(cellMappingSerial);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(revisionMapping);
        ArgumentNullException.ThrowIfNull(revisionMappingSerial);

        var payload = new BinaryWriterEx();

        var manifest = new BinaryWriterEx();
        manifestMapping.Serialize(manifest);
        manifestMappingSerial.Serialize(manifest);
        WriteStorageIndexObject(
            payload,
            StreamObjectTypeHeaderStart.StorageIndexManifestMapping,
            manifest.ToArray());

        var cell = new BinaryWriterEx();
        cellId.Serialize(cell);
        cellMapping.Serialize(cell);
        cellMappingSerial.Serialize(cell);
        WriteStorageIndexObject(
            payload,
            StreamObjectTypeHeaderStart.StorageIndexCellMapping,
            cell.ToArray());

        var revisionData = new BinaryWriterEx();
        revision.Serialize(revisionData);
        revisionMapping.Serialize(revisionData);
        revisionMappingSerial.Serialize(revisionData);
        WriteStorageIndexObject(
            payload,
            StreamObjectTypeHeaderStart.StorageIndexRevisionMapping,
            revisionData.ToArray());

        return new DataElement(
            DataElementType.StorageIndexDataElementData,
            dataElementGuid,
            serialNumber)
        {
            Data = payload.ToArray(),
        };
    }

    private static void WriteStorageIndexObject(
        BinaryWriterEx output,
        StreamObjectTypeHeaderStart type,
        byte[] data)
    {
        var header = new StreamObjectHeaderStart16Bit(type, data.Length);
        header.Serialize(output);
        output.WriteBytes(data);
    }

    /// <summary>Creates the data-element-only 13/11 response shape.</summary>
    public static FsshttpbResponse BuildStorageIndexOnlyQueryChangesResponse(
        ulong requestId,
        ExGuid storageIndex,
        ulong knowledgeSequence,
        bool emitNullManifestMapping = true,
        bool includeCellKnowledge = true,
        ExGuid? waterlineCellStorage = null)
    {
        ArgumentNullException.ThrowIfNull(storageIndex);
        var serialGuid = storageIndex.Guid == Guid.Empty ? Guid.NewGuid() : storageIndex.Guid;
        var element = BuildStorageIndexDataElement(
            storageIndex,
            new SerialNumber(serialGuid, storageIndex.Value + 1),
            emitNullManifestMapping);

        return new FsshttpbResponse
        {
            ProtocolVersion = 13,
            MinimumVersion = 11,
            DataElementPackage = new DataElementPackage { DataElements = { element } },
            SubResponses =
            {
                new FsshttpbSubResponse
                {
                    RequestId = requestId,
                    RequestType = RequestTypes.QueryChanges,
                    Status = false,
                    Data = new QueryChangesSubResponseData
                    {
                        StorageIndexExtendedGuid = storageIndex,
                        CellKnowledgeCellGuid = storageIndex.Guid,
                        CellKnowledgeTo = knowledgeSequence,
                        IncludeCellKnowledge = includeCellKnowledge,
                        WaterlineCellStorageExtendedGuid = waterlineCellStorage,
                        Waterline = knowledgeSequence,
                    },
                },
            },
        };
    }

    /// <summary>Stable identifiers supplied by a document store.</summary>
    public sealed record StableIdentity(
        ExGuid StorageManifestGuid,
        ExGuid CellManifestGuid,
        ExGuid RevisionManifestGuid,
        ExGuid ObjectGroupGuid,
        ExGuid ObjectDataBlobGuid,
        ExGuid ObjectGuid,
        ExGuid RevisionId,
        CellId CellId,
        Guid SerialGuid);

    public static FsshttpbResponse BuildQueryChangesResponse(
        ulong requestId,
        byte[] fileContent,
        StableIdentity identity,
        ulong knowledgeSequence)
        => BuildQueryChangesResponse(requestId, fileContent,
            identity.StorageManifestGuid,
            identity.CellManifestGuid,
            identity.RevisionManifestGuid,
            identity.ObjectGroupGuid,
            identity.ObjectDataBlobGuid,
            identity.ObjectGuid,
            identity.RevisionId,
            identity.CellId,
            identity.SerialGuid,
            knowledgeSequence);

    /// <summary>
    /// The storage manifest schema GUID used by SharePoint and by the
    /// MS-FSSHTTPB interop stack. This is a protocol identity, rather than a
    /// per-document value.
    /// </summary>
    public static readonly Guid StorageManifestSchemaGuid =
        new("0EB93394-571D-41E9-AAD3-880D92D31955");

    /// <summary>The fixed root GUID used by the storage and revision manifests.</summary>
    public static readonly Guid RootExtendedGuid =
        new("84DEFAB9-AAA3-4A0D-A3A8-520C77AC7073");

    /// <summary>The fixed second extended GUID used by the root cell ID.</summary>
    public static readonly Guid CellSecondExtendedGuid =
        new("6F2A4665-42C8-46C7-BAB4-E28FDCE1E32B");

    /// <summary>
    /// Builds an Object Data BLOB data element whose payload is the raw file
    /// content. [MS-FSSHTTPB] section 2.2.1.12.8.
    ///
    /// Wire layout of the data element payload:
    /// <code>
    /// Object Data BLOB (16/32-bit Stream Object Header, type 0x02)
    /// Data (variable): the raw file bytes
    /// </code>
    /// </summary>
    /// <param name="dataElementGuid">The extended GUID identifying this data element.</param>
    /// <param name="serialNumber">The serial number of this data element.</param>
    /// <param name="content">The raw file bytes to serve.</param>
    /// <returns>A data element of type <see cref="DataElementType.ObjectDataBLOBDataElementData"/>.</returns>
    public static DataElement BuildObjectDataBlobDataElement(
        ExGuid dataElementGuid,
        SerialNumber serialNumber,
        byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var payloadWriter = new BinaryWriterEx();

        // Object Data BLOB stream object: header + raw data.
        var blobHeader = new StreamObjectHeaderStart32Bit(
            StreamObjectTypeHeaderStart.ObjectDataBLOB, content.Length);
        blobHeader.Serialize(payloadWriter);
        payloadWriter.WriteBytes(content);

        return new DataElement(
            DataElementType.ObjectDataBLOBDataElementData,
            dataElementGuid,
            serialNumber)
        {
            Data = payloadWriter.ToArray(),
        };
    }

    /// <summary>
    /// Builds a Storage Manifest data element. [MS-FSSHTTPB] section 2.2.1.12.3.
    ///
    /// Wire layout of the data element payload:
    /// <code>
    /// Storage Manifest Schema GUID (16-bit header, type 0x0C) + GUID (16 bytes)
    /// Storage Manifest Root Declare (16-bit header, type 0x07)
    ///   Root Extended GUID (variable)
    ///   Cell ID (variable)
    /// </code>
    /// </summary>
    /// <param name="dataElementGuid">The extended GUID identifying this data element.</param>
    /// <param name="serialNumber">The serial number of this data element.</param>
    /// <param name="rootDeclare">The root storage manifest extended GUID.</param>
    /// <param name="cellId">The cell identifier for the root declare.</param>
    /// <returns>A data element of type <see cref="DataElementType.StorageManifestDataElementData"/>.</returns>
    public static DataElement BuildStorageManifestDataElement(
        ExGuid dataElementGuid,
        SerialNumber serialNumber,
        ExGuid rootDeclare,
        CellId cellId)
    {
        var payloadWriter = new BinaryWriterEx();

        // Storage Manifest Schema GUID: 16-bit header + 16 raw GUID bytes.
        var schemaHeader = new StreamObjectHeaderStart16Bit(
            StreamObjectTypeHeaderStart.StorageManifestSchemaGUID, 16);
        schemaHeader.Serialize(payloadWriter);
        ExGuid.WriteGuid(payloadWriter, StorageManifestSchemaGuid);

        // Storage Manifest Root Declare: 16-bit header + Root ExGuid + Cell ID.
        var rootWriter = new BinaryWriterEx();
        rootDeclare.Serialize(rootWriter);
        cellId.Serialize(rootWriter);
        byte[] rootPayload = rootWriter.ToArray();

        var rootHeader = new StreamObjectHeaderStart16Bit(
            StreamObjectTypeHeaderStart.StorageManifestRootDeclare, rootPayload.Length);
        rootHeader.Serialize(payloadWriter);
        payloadWriter.WriteBytes(rootPayload);

        return new DataElement(
            DataElementType.StorageManifestDataElementData,
            dataElementGuid,
            serialNumber)
        {
            Data = payloadWriter.ToArray(),
        };
    }

    /// <summary>
    /// Builds a Cell Manifest data element. [MS-FSSHTTPB] section 2.2.1.12.4.
    ///
    /// Wire layout of the data element payload:
    /// <code>
    /// Cell Manifest Current Revision (16-bit header, type 0x0B)
    ///   Cell Manifest Current Revision Extended GUID (variable)
    /// </code>
    /// </summary>
    /// <param name="dataElementGuid">The extended GUID identifying this data element.</param>
    /// <param name="serialNumber">The serial number of this data element.</param>
    /// <param name="currentRevision">The extended GUID of the current revision.</param>
    /// <returns>A data element of type <see cref="DataElementType.CellManifestDataElementData"/>.</returns>
    public static DataElement BuildCellManifestDataElement(
        ExGuid dataElementGuid,
        SerialNumber serialNumber,
        ExGuid currentRevision)
    {
        var payloadWriter = new BinaryWriterEx();

        // Cell Manifest Current Revision: 16-bit header + revision ExGuid.
        var revisionWriter = new BinaryWriterEx();
        currentRevision.Serialize(revisionWriter);
        byte[] revisionPayload = revisionWriter.ToArray();

        var revisionHeader = new StreamObjectHeaderStart16Bit(
            StreamObjectTypeHeaderStart.CellManifestCurrentRevision, revisionPayload.Length);
        revisionHeader.Serialize(payloadWriter);
        payloadWriter.WriteBytes(revisionPayload);

        return new DataElement(
            DataElementType.CellManifestDataElementData,
            dataElementGuid,
            serialNumber)
        {
            Data = payloadWriter.ToArray(),
        };
    }

    /// <summary>
    /// Builds a Revision Manifest data element. [MS-FSSHTTPB] section 2.2.1.12.5.
    ///
    /// Wire layout of the data element payload:
    /// <code>
    /// Revision Manifest (16-bit header, type 0x1A)
    ///   Revision ID (variable)
    ///   Base Revision ID (variable)
    /// Revision Manifest Root Declare (16-bit header, type 0x0A)
    ///   Root Extended GUID (variable)
    ///   Object Extended GUID (variable)
    /// Revision Manifest Object Group References (16-bit header, type 0x19)
    ///   Object Group Extended GUID (variable)
    /// </code>
    /// </summary>
    /// <param name="dataElementGuid">The extended GUID identifying this data element.</param>
    /// <param name="serialNumber">The serial number of this data element.</param>
    /// <param name="revisionId">The revision identifier this element represents.</param>
    /// <param name="baseRevisionId">The base revision identifier (may be <see cref="ExGuid.Null"/>).</param>
    /// <param name="rootDeclare">The root revision extended GUID.</param>
    /// <param name="objectDeclare">The object extended GUID for the root declare.</param>
    /// <param name="objectGroup">The object group extended GUID reference.</param>
    /// <returns>A data element of type <see cref="DataElementType.RevisionManifestDataElementData"/>.</returns>
    public static DataElement BuildRevisionManifestDataElement(
        ExGuid dataElementGuid,
        SerialNumber serialNumber,
        ExGuid revisionId,
        ExGuid baseRevisionId,
        ExGuid rootDeclare,
        ExGuid objectDeclare,
        ExGuid objectGroup)
        => BuildRevisionManifestDataElement(
            dataElementGuid, serialNumber, revisionId, baseRevisionId,
            rootDeclare, objectDeclare, new[] { objectGroup });

    /// <summary>Builds a revision manifest with all referenced object groups.</summary>
    public static DataElement BuildRevisionManifestDataElement(
        ExGuid dataElementGuid,
        SerialNumber serialNumber,
        ExGuid revisionId,
        ExGuid baseRevisionId,
        ExGuid rootDeclare,
        ExGuid objectDeclare,
        IEnumerable<ExGuid> objectGroups)
    {
        ArgumentNullException.ThrowIfNull(objectGroups);
        var payloadWriter = new BinaryWriterEx();

        // Revision Manifest: 16-bit header + Revision ID + Base Revision ID.
        var manifestWriter = new BinaryWriterEx();
        revisionId.Serialize(manifestWriter);
        baseRevisionId.Serialize(manifestWriter);
        byte[] manifestPayload = manifestWriter.ToArray();

        var manifestHeader = new StreamObjectHeaderStart16Bit(
            StreamObjectTypeHeaderStart.RevisionManifest, manifestPayload.Length);
        manifestHeader.Serialize(payloadWriter);
        payloadWriter.WriteBytes(manifestPayload);

        // Revision Manifest Root Declare: 16-bit header + Root ExGuid + Object ExGuid.
        var rootWriter = new BinaryWriterEx();
        rootDeclare.Serialize(rootWriter);
        objectDeclare.Serialize(rootWriter);
        byte[] rootPayload = rootWriter.ToArray();

        var rootHeader = new StreamObjectHeaderStart16Bit(
            StreamObjectTypeHeaderStart.RevisionManifestRootDeclare, rootPayload.Length);
        rootHeader.Serialize(payloadWriter);
        payloadWriter.WriteBytes(rootPayload);

        // Revision Manifest Object Group References: one object-group ExGuid
        // per reference, in the order supplied by the caller.
        foreach (var objectGroup in objectGroups)
        {
            ArgumentNullException.ThrowIfNull(objectGroup);
            var groupWriter = new BinaryWriterEx();
            objectGroup.Serialize(groupWriter);
            byte[] groupPayload = groupWriter.ToArray();
            var groupHeader = new StreamObjectHeaderStart16Bit(
                StreamObjectTypeHeaderStart.RevisionManifestObjectGroupReferences,
                groupPayload.Length);
            groupHeader.Serialize(payloadWriter);
            payloadWriter.WriteBytes(groupPayload);
        }

        return new DataElement(
            DataElementType.RevisionManifestDataElementData,
            dataElementGuid,
            serialNumber)
        {
            Data = payloadWriter.ToArray(),
        };
    }

    /// <summary>
    /// Builds an Object Group data element. [MS-FSSHTTPB] section 2.2.1.12.6.
    ///
    /// Wire layout of the data element payload:
    /// <code>
    /// Object Group Declarations (compound, type 0x1D)
    ///   Object Group Object Declare (16-bit header, type 0x18)
    ///     Object Extended GUID, Object Partition ID, Object Data Size,
    ///     Object References Count, Cell References Count
    ///   Object Group Declarations End (8-bit header, type 0x1D)
    /// Object Group Data (compound, type 0x1E)
    ///   Object Group Object Data (16/32-bit header, type 0x16)
    ///     Object Extended GUID Array, Cell ID Array, Data (Binary Item)
    ///   Object Group Data End (8-bit header, type 0x1E)
    /// </code>
    /// </summary>
    /// <param name="dataElementGuid">The extended GUID identifying this data element.</param>
    /// <param name="serialNumber">The serial number of this data element.</param>
    /// <param name="objectGuid">The extended GUID of the declared object.</param>
    /// <param name="objectDataSize">The size in bytes of the object's binary data.</param>
    /// <param name="objectData">The object's binary data (opaque to this protocol).</param>
    /// <returns>A data element of type <see cref="DataElementType.ObjectGroupDataElementData"/>.</returns>
    public static DataElement BuildObjectGroupDataElement(
        ExGuid dataElementGuid,
        SerialNumber serialNumber,
        ExGuid objectGuid,
        ulong objectDataSize,
        byte[] objectData)
    {
        ArgumentNullException.ThrowIfNull(objectData);

        var payloadWriter = new BinaryWriterEx();

        // --- Object Group Declarations (compound) ---
        var declarationsWriter = new BinaryWriterEx();

        // Object Group Object Declare: 16-bit header + 5 fields.
        var declareWriter = new BinaryWriterEx();
        objectGuid.Serialize(declareWriter);
        new Compact64bitInt(1).Serialize(declareWriter);          // Object Partition ID
        new Compact64bitInt(objectDataSize).Serialize(declareWriter); // Object Data Size
        new Compact64bitInt(0).Serialize(declareWriter);          // Opaque data has no object references
        new Compact64bitInt(0).Serialize(declareWriter);          // Cell References Count
        byte[] declarePayload = declareWriter.ToArray();

        var declareHeader = new StreamObjectHeaderStart16Bit(
            StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare, declarePayload.Length);
        declareHeader.Serialize(declarationsWriter);
        declarationsWriter.WriteBytes(declarePayload);

        byte[] declarationsPayload = declarationsWriter.ToArray();
        var declarationsHeader = new StreamObjectHeaderStart32Bit(
            StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0);
        declarationsHeader.Serialize(payloadWriter);
        payloadWriter.WriteBytes(declarationsPayload);
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations)
            .Serialize(payloadWriter);

        // --- Object Group Data (compound) ---
        var dataWriter = new BinaryWriterEx();

        // Object Group Object Data: 16/32-bit header + ExGUIDArray + CellIDArray + BinaryItem.
        var objectDataWriter = new BinaryWriterEx();

        // Object Extended GUID Array: compact count + ExGuids.
        new Compact64bitInt(0).Serialize(objectDataWriter);

        // Cell ID Array: compact count (0) + no entries.
        new Compact64bitInt(0).Serialize(objectDataWriter);

        // Data: Binary Item (compact length + raw bytes).
        new BinaryItem(objectData).Serialize(objectDataWriter);

        byte[] objectDataPayload = objectDataWriter.ToArray();
        var objectDataHeader = new StreamObjectHeaderStart32Bit(
            StreamObjectTypeHeaderStart.ObjectGroupObjectData, objectDataPayload.Length);
        objectDataHeader.Serialize(dataWriter);
        dataWriter.WriteBytes(objectDataPayload);

        byte[] dataPayload = dataWriter.ToArray();
        var dataHeader = new StreamObjectHeaderStart32Bit(
            StreamObjectTypeHeaderStart.ObjectGroupData, 0);
        dataHeader.Serialize(payloadWriter);
        payloadWriter.WriteBytes(dataPayload);
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupData)
            .Serialize(payloadWriter);

        return new DataElement(
            DataElementType.ObjectGroupDataElementData,
            dataElementGuid,
            serialNumber)
        {
            Data = payloadWriter.ToArray(),
        };
    }

    /// <summary>
    /// Builds a complete Query Changes response that serves the given file
    /// content. [MS-FSSHTTPB] sections 2.2.1.12.3 through 2.2.1.12.8 and
    /// 2.2.3.1.2.
    ///
    /// The response contains a single sub-response of type
    /// <see cref="RequestTypes.QueryChanges"/> whose
    /// <see cref="QueryChangesSubResponseData.StorageIndexExtendedGuid"/>
    /// identifies the cell storage whose knowledge is returned. The
    /// <see cref="FsshttpbResponse.DataElementPackage"/> carries the full
    /// data element graph:
    /// <list type="number">
    /// <item>Storage Manifest (root) — schema GUID + root cell declare.</item>
    /// <item>Cell Manifest — current revision.</item>
    /// <item>Revision Manifest — revision + object group reference.</item>
    /// <item>Object Group — object declaration + raw file bytes.</item>
    /// <item>Storage Index — identifies the returned cell storage.</item>
    /// </list>
    /// All extended GUIDs and serial numbers are freshly generated.
    /// </summary>
    /// <param name="requestId">The ID of the sub-request this response answers.</param>
    /// <param name="fileContent">The raw file bytes to serve.</param>
    /// <returns>A complete <see cref="FsshttpbResponse"/>.</returns>
    public static FsshttpbResponse BuildQueryChangesResponse(
        ulong requestId,
        byte[] fileContent)
        => BuildQueryChangesResponse(requestId, fileContent, null, null, null, null, null, null, null, null, null, null);

    /// <summary>Builds a response using stable document protocol identity.</summary>
    public static FsshttpbResponse BuildQueryChangesResponse(
        ulong requestId,
        byte[] fileContent,
        ExGuid? storageManifestGuid,
        ExGuid? cellManifestGuid,
        ExGuid? revisionManifestGuid,
        ExGuid? objectGroupGuid,
        ExGuid? objectDataBlobGuid,
        ExGuid? objectGuid,
        ExGuid? revisionId,
        CellId? cellId,
        Guid? serialGuid,
        ulong? knowledgeSequence)
    {
        ArgumentNullException.ThrowIfNull(fileContent);

        // The current wire layout uses the 3-byte compact-integer form in
        // both Knowledge entries, matching the MS-FSSHTTPB example headers
        // (CellKnowledgeRange length 20 and WaterlineKnowledgeEntry length 21).
        ulong responseKnowledge = Math.Max(knowledgeSequence ?? (ulong)fileContent.Length, 73507UL);

        storageManifestGuid ??= new ExGuid(1, Guid.NewGuid());
        cellManifestGuid ??= new ExGuid(2, Guid.NewGuid());
        revisionManifestGuid ??= new ExGuid(3, Guid.NewGuid());
        objectGroupGuid ??= new ExGuid(4, Guid.NewGuid());
        objectDataBlobGuid ??= new ExGuid(5, Guid.NewGuid());
        objectGuid ??= new ExGuid(6, Guid.NewGuid());
        revisionId ??= new ExGuid(7, Guid.NewGuid());
        var baseRevisionId = ExGuid.Null;
        cellId ??= new CellId(
            new ExGuid(1, RootExtendedGuid),
            new ExGuid(1, CellSecondExtendedGuid));

        // The manifest data-element IDs are document-specific, but the root
        // declared by both StorageManifest and RevisionManifest is the fixed
        // protocol root. SharePoint emits ExGuid(2, RootExtendedGuid) here.
        var manifestRoot = new ExGuid(2, RootExtendedGuid);

        serialGuid ??= Guid.NewGuid();
        Guid serial = serialGuid.Value;
        var storageManifestSerial = new SerialNumber(serial, 1);
        var cellManifestSerial = new SerialNumber(serial, 2);
        var revisionManifestSerial = new SerialNumber(serial, 3);
        var objectGroupSerial = new SerialNumber(serial, 4);
        var objectDataBlobSerial = new SerialNumber(serial, 5);

        // Build the data element graph.
          ExGuid storageManifest = storageManifestGuid;
          ExGuid cellManifest = cellManifestGuid;
          ExGuid revisionManifest = revisionManifestGuid;
          ExGuid objectGroup = objectGroupGuid;
          ExGuid objectDataBlob = objectDataBlobGuid;
          ExGuid objectIdentifier = objectGuid;
          ExGuid revision = revisionId;
          CellId cell = cellId;

        var storageManifestElement = BuildStorageManifestDataElement(
            storageManifest, storageManifestSerial, manifestRoot, cell);

        var cellManifestElement = BuildCellManifestDataElement(
            cellManifest, cellManifestSerial, revision);

        var revisionManifestElement = BuildRevisionManifestDataElement(
            revisionManifest, revisionManifestSerial,
            revision, baseRevisionId,
            manifestRoot, objectIdentifier, objectGroup);

        var objectGroupElement = BuildObjectGroupDataElement(
            objectGroup, objectGroupSerial,
               objectIdentifier, (ulong)fileContent.Length, fileContent);

        // The file bytes are carried by ObjectGroupObjectData. An
        // ObjectDataBLOB is a different graph form and must not be appended
        // as an orphan element: SharePoint's QueryChanges graph references
        // the object group from the revision manifest and the object group
        // carries the object data itself.
        var storageIndexElement = BuildStorageIndexDataElement(
            objectDataBlob,
            objectDataBlobSerial,
            storageManifest,
            storageManifestSerial,
            cell,
            cellManifest,
            cellManifestSerial,
            revision,
            revisionManifest,
            revisionManifestSerial);

        var package = new DataElementPackage
        {
            DataElements =
            {
                storageManifestElement,
                cellManifestElement,
                revisionManifestElement,
                objectGroupElement,
                storageIndexElement,
            },
        };

        // Build the sub-response.
        var subResponse = new FsshttpbSubResponse
        {
            RequestId = requestId,
            RequestType = RequestTypes.QueryChanges,
            Status = false,
            Data = new QueryChangesSubResponseData
            {
                StorageIndexExtendedGuid = objectDataBlob,
                CellKnowledgeCellGuid = cell.LongId.Guid,
                CellKnowledgeTo = responseKnowledge,
                Waterline = responseKnowledge + 2000,
            },
        };

        return new FsshttpbResponse
        {
            Status = false,
            DataElementPackage = package,
            SubResponses = { subResponse },
        };
    }
}
