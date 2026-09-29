namespace OfficeCollabServer.FssHttpB;

/// <summary>
/// The request type enumeration for MS-FSSHTTPB sub-requests.
/// </summary>
public enum RequestTypes
{
    /// <summary>Query Access.</summary>
    QueryAccess = 1,

    /// <summary>Query Changes.</summary>
    QueryChanges = 2,

    /// <summary>Query Knowledge.</summary>
    QueryKnowledge = 3,

    /// <summary>Put Changes.</summary>
    PutChanges = 5,

    /// <summary>Query Raw Storage.</summary>
    QueryRawStorage = 6,

    /// <summary>Put Raw Storage.</summary>
    PutRawStorage = 7,

    /// <summary>Query Diagnostic Store Info.</summary>
    QueryDiagnosticStoreInfo = 8,

    /// <summary>Allocate Extended GUID Range.</summary>
    AllocateExtendedGuidRange = 11,
}

/// <summary>
/// The filter type enumeration for Query Changes filters.
/// </summary>
public enum FilterType
{
    /// <summary>All filter.</summary>
    AllFilter = 1,

    /// <summary>Data element type filter.</summary>
    DataElementTypeFilter = 2,

    /// <summary>Storage index referenced data elements filter.</summary>
    StorageIndexReferencedDataElementsFilter = 3,

    /// <summary>Cell ID filter.</summary>
    CellIDFilter = 4,

    /// <summary>Custom filter.</summary>
    CustomFilter = 5,

    /// <summary>Data element IDs filter.</summary>
    DataElementIDsFilter = 6,

    /// <summary>Hierarchy filter.</summary>
    HierarchyFilter = 7,
}

/// <summary>
/// The data element type enumeration.
/// </summary>
public enum DataElementType
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Storage index data element data.</summary>
    StorageIndexDataElementData = 1,

    /// <summary>Storage manifest data element data.</summary>
    StorageManifestDataElementData = 2,

    /// <summary>Cell manifest data element data.</summary>
    CellManifestDataElementData = 3,

    /// <summary>Revision manifest data element data.</summary>
    RevisionManifestDataElementData = 4,

    /// <summary>Object group data element data.</summary>
    ObjectGroupDataElementData = 5,

    /// <summary>Fragment data element data.</summary>
    FragmentDataElementData = 6,

    /// <summary>Object data BLOB data element data.</summary>
    ObjectDataBLOBDataElementData = 10,
}

/// <summary>
/// The hierarchy filter depth enumeration.
/// </summary>
public enum HierarchyFilterDepth
{
    /// <summary>Index only.</summary>
    IndexOnly = 0,

    /// <summary>First data element.</summary>
    FirstDataElement = 1,

    /// <summary>Single level.</summary>
    SingleLevel = 2,

    /// <summary>Deep.</summary>
    Deep = 3,
}

/// <summary>
/// The knowledge type enumeration.
/// </summary>
public enum KnowledgeType
{
    /// <summary>Cell knowledge.</summary>
    CellKnowledge = 0,

    /// <summary>Waterline knowledge.</summary>
    WaterlineKnowledge = 1,

    /// <summary>Fragment knowledge.</summary>
    FragmentKnowledge = 2,

    /// <summary>Content tag knowledge.</summary>
    ContentTagKnowledge = 3,
}

/// <summary>
/// The stream object type header start enumeration. Values identify the
/// stream object type in a 16-bit or 32-bit stream object header start.
/// </summary>
public enum StreamObjectTypeHeaderStart
{
    /// <summary>Data element.</summary>
    DataElement = 0x01,

    /// <summary>Object data BLOB.</summary>
    ObjectDataBLOB = 0x02,

    /// <summary>Waterline knowledge entry.</summary>
    WaterlineKnowledgeEntry = 0x04,

    /// <summary>Object group object BLOB data declaration.</summary>
    ObjectGroupObjectBLOBDataDeclaration = 0x05,

    /// <summary>Data element hash.</summary>
    DataElementHash = 0x06,

    /// <summary>Storage manifest root declare.</summary>
    StorageManifestRootDeclare = 0x07,

    /// <summary>Revision manifest root declare.</summary>
    RevisionManifestRootDeclare = 0x0A,

    /// <summary>Cell manifest current revision.</summary>
    CellManifestCurrentRevision = 0x0B,

    /// <summary>Storage manifest schema GUID.</summary>
    StorageManifestSchemaGUID = 0x0C,

    /// <summary>Storage index revision mapping.</summary>
    StorageIndexRevisionMapping = 0x0D,

    /// <summary>Storage index cell mapping.</summary>
    StorageIndexCellMapping = 0x0E,

    /// <summary>Cell knowledge range.</summary>
    CellKnowledgeRange = 0x0F,

    /// <summary>Knowledge.</summary>
    Knowledge = 0x10,

    /// <summary>Storage index manifest mapping.</summary>
    StorageIndexManifestMapping = 0x11,

    /// <summary>Cell knowledge.</summary>
    CellKnowledge = 0x14,

    /// <summary>Data element package.</summary>
    DataElementPackage = 0x15,

    /// <summary>Object group object data.</summary>
    ObjectGroupObjectData = 0x16,

    /// <summary>Cell knowledge entry.</summary>
    CellKnowledgeEntry = 0x17,

    /// <summary>Object group object declare.</summary>
    ObjectGroupObjectDeclare = 0x18,

    /// <summary>Revision manifest object group references.</summary>
    RevisionManifestObjectGroupReferences = 0x19,

    /// <summary>Revision manifest.</summary>
    RevisionManifest = 0x1A,

    /// <summary>Object group object data BLOB reference.</summary>
    ObjectGroupObjectDataBLOBReference = 0x1C,

    /// <summary>Object group declarations.</summary>
    ObjectGroupDeclarations = 0x1D,

    /// <summary>Object group data.</summary>
    ObjectGroupData = 0x1E,

    /// <summary>Leaf node object.</summary>
    LeafNodeObject = 0x1F,

    /// <summary>Intermediate node object.</summary>
    IntermediateNodeObject = 0x20,

    /// <summary>FSSHTTPD node signature object.</summary>
    SignatureObject = 0x21,

    /// <summary>FSSHTTPD node represented-data size object.</summary>
    DataSizeObject = 0x22,

    /// <summary>Waterline knowledge.</summary>
    WaterlineKnowledge = 0x29,

    /// <summary>Content tag knowledge.</summary>
    ContentTagKnowledge = 0x2D,

    /// <summary>Content tag knowledge entry.</summary>
    ContentTagKnowledgeEntry = 0x2E,

    /// <summary>Request.</summary>
    Request = 0x040,

    /// <summary>FSSHTTPB sub-response.</summary>
    FsshttpbSubResponse = 0x041,

    /// <summary>Sub-request.</summary>
    SubRequest = 0x042,

    /// <summary>Read access response.</summary>
    ReadAccessResponse = 0x043,

    /// <summary>Specialized knowledge.</summary>
    SpecializedKnowledge = 0x044,

    /// <summary>Put changes response serial number reassign all.</summary>
    PutChangesResponseSerialNumberReassignAll = 0x045,

    /// <summary>Write access response.</summary>
    WriteAccessResponse = 0x046,

    /// <summary>Query changes filter.</summary>
    QueryChangesFilter = 0x047,

    /// <summary>Win32 error.</summary>
    Win32Error = 0x049,

    /// <summary>Protocol error.</summary>
    ProtocolError = 0x04B,

    /// <summary>Response error.</summary>
    ResponseError = 0x04D,

    /// <summary>User agent version.</summary>
    UserAgentVersion = 0x04F,

    /// <summary>Query changes filter schema specific.</summary>
    QueryChangesFilterSchemaSpecific = 0x050,

    /// <summary>Query changes request.</summary>
    QueryChangesRequest = 0x051,

    /// <summary>HRESULT error.</summary>
    HRESULTError = 0x052,

    /// <summary>Put changes response serial number reassign.</summary>
    PutChangesResponseSerialNumberReassign = 0x053,

    /// <summary>Query changes filter data element IDs.</summary>
    QueryChangesFilterDataElementIDs = 0x054,

    /// <summary>User agent GUID.</summary>
    UserAgentGUID = 0x055,

    /// <summary>Query changes filter data element type.</summary>
    QueryChangesFilterDataElementType = 0x057,

    /// <summary>Query changes data constraint.</summary>
    QueryChangesDataConstraint = 0x059,

    /// <summary>Put changes request.</summary>
    PutChangesRequest = 0x05A,

    /// <summary>Query changes request arguments.</summary>
    QueryChangesRequestArguments = 0x05B,

    /// <summary>Query changes filter cell ID.</summary>
    QueryChangesFilterCellID = 0x05C,

    /// <summary>User agent.</summary>
    UserAgent = 0x05D,

    /// <summary>Query changes response.</summary>
    QueryChangesResponse = 0x05F,

    /// <summary>Query changes filter hierarchy.</summary>
    QueryChangesFilterHierarchy = 0x060,

    /// <summary>FSSHTTPB response.</summary>
    FsshttpbResponse = 0x062,

    /// <summary>Data element fragment.</summary>
    DataElementFragment = 0x06A,

    /// <summary>Fragment knowledge.</summary>
    FragmentKnowledge = 0x06B,

    /// <summary>Fragment knowledge entry.</summary>
    FragmentKnowledgeEntry = 0x06C,

    /// <summary>Object group metadata.</summary>
    ObjectGroupMetadata = 0x78,

    /// <summary>Object group metadata declarations.</summary>
    ObjectGroupMetadataDeclarations = 0x79,

    /// <summary>Allocate extended GUID range request.</summary>
    AllocateExtendedGUIDRangeRequest = 0x080,

    /// <summary>Allocate extended GUID range response.</summary>
    AllocateExtendedGUIDRangeResponse = 0x081,

    /// <summary>Target partition ID.</summary>
    TargetPartitionId = 0x083,

    /// <summary>Put changes lock ID.</summary>
    PutChangesLockId = 0x85,

    /// <summary>Additional flags.</summary>
    AdditionalFlags = 0x86,

    /// <summary>Put changes response.</summary>
    PutChangesResponse = 0x087,

    /// <summary>Request hash options.</summary>
    RequestHashOptions = 0x088,

    /// <summary>Diagnostic request option output.</summary>
    DiagnosticRequestOptionOutput = 0x089,

    /// <summary>Diagnostic request option input.</summary>
    DiagnosticRequestOptionInput = 0x08A,

    /// <summary>User agent client and platform.</summary>
    UserAgentClientandPlatform = 0x08B,

    /// <summary>Version token knowledge.</summary>
    VersionTokenKnowledge = 0x8C,

    /// <summary>Cell roundtrip options.</summary>
    CellRoundtripOptions = 0x8D,

    /// <summary>File hash.</summary>
    FileHash = 0x8E,

    /// <summary>Query changes versioning.</summary>
    QueryChangesVersioning = 0x30,

    /// <summary>Error string supplemental info.</summary>
    ErrorStringSupplementalInfo = 0x4E,

    /// <summary>Query data element request.</summary>
    QueryDataElementRequest = 0x065,

    /// <summary>Cell error.</summary>
    CellError = 0x066,

    /// <summary>Query changes filter flags.</summary>
    QueryChangesFilterFlags = 0x068,
}

/// <summary>
/// The stream object type header end enumeration. Values identify the
/// stream object type in an 8-bit or 16-bit stream object header end.
/// </summary>
public enum StreamObjectTypeHeaderEnd
{
    /// <summary>Data element.</summary>
    DataElement = 0x01,

    /// <summary>Knowledge.</summary>
    Knowledge = 0x10,

    /// <summary>Cell knowledge.</summary>
    CellKnowledge = 0x14,

    /// <summary>Data element package.</summary>
    DataElementPackage = 0x15,

    /// <summary>Object group declarations.</summary>
    ObjectGroupDeclarations = 0x1D,

    /// <summary>Object group data.</summary>
    ObjectGroupData = 0x1E,

    /// <summary>Intermediate node end.</summary>
    IntermediateNodeEnd = 0x1F,

    /// <summary>Root node end.</summary>
    RootNodeEnd = 0x20,

    /// <summary>Waterline knowledge.</summary>
    WaterlineKnowledge = 0x29,

    /// <summary>Content tag knowledge.</summary>
    ContentTagKnowledge = 0x2D,

    /// <summary>Request.</summary>
    Request = 0x040,

    /// <summary>Sub-response.</summary>
    SubResponse = 0x041,

    /// <summary>Sub-request.</summary>
    SubRequest = 0x042,

    /// <summary>Read access response.</summary>
    ReadAccessResponse = 0x043,

    /// <summary>Specialized knowledge.</summary>
    SpecializedKnowledge = 0x044,

    /// <summary>Write access response.</summary>
    WriteAccessResponse = 0x046,

    /// <summary>Query changes filter.</summary>
    QueryChangesFilter = 0x047,

    /// <summary>Error.</summary>
    Error = 0x04D,

    /// <summary>Query changes request.</summary>
    QueryChangesRequest = 0x051,

    /// <summary>User agent.</summary>
    UserAgent = 0x05D,

    /// <summary>Response.</summary>
    Response = 0x062,

    /// <summary>Fragment knowledge.</summary>
    FragmentKnowledge = 0x06B,

    /// <summary>Object group metadata declarations.</summary>
    ObjectGroupMetadataDeclarations = 0x79,

    /// <summary>Alternative packaging.</summary>
    AlternativePackaging = 0x7A,

    /// <summary>Target partition ID.</summary>
    TargetPartitionId = 0x083,
}

/// <summary>
/// The cell error code enumeration.
/// </summary>
public enum CellErrorCode
{
    /// <summary>Unknown error.</summary>
    UnknownError = 1,

    /// <summary>Invalid object.</summary>
    InvalidObject = 2,

    /// <summary>Invalid partition.</summary>
    InvalidPartition = 3,

    /// <summary>Request not supported.</summary>
    RequestNotSupported = 4,

    /// <summary>Storage read-only.</summary>
    StorageReadOnly = 5,

    /// <summary>Revision ID not found.</summary>
    RevisionIDNotFound = 6,

    /// <summary>Bad token.</summary>
    BadToken = 7,

    /// <summary>Request not finished.</summary>
    RequestNotFinished = 8,

    /// <summary>Incompatible token.</summary>
    IncompatibleToken = 9,

    /// <summary>Scoped cell storage.</summary>
    ScopedCellStorage = 11,

    /// <summary>Coherency failure.</summary>
    CoherencyFailure = 12,

    /// <summary>Cell storage state deserialization failure.</summary>
    CellStorageStateDeserializationFailure = 13,

    /// <summary>Incompatible protocol version.</summary>
    IncompatibleProtocolVersion = 15,

    /// <summary>Referenced data element not found.</summary>
    ReferencedDataElementNotFound = 16,

    /// <summary>Request stream schema error.</summary>
    RequestStreamSchemaError = 18,

    /// <summary>Response stream schema error.</summary>
    ResponseStreamSchemaError = 19,

    /// <summary>Unknown request.</summary>
    UnknownRequest = 20,

    /// <summary>Storage failure.</summary>
    StorageFailure = 21,

    /// <summary>Storage write only.</summary>
    StorageWriteOnly = 22,

    /// <summary>Invalid serialization.</summary>
    InvalidSerialization = 23,

    /// <summary>Data element not found.</summary>
    DataElementNotFound = 24,

    /// <summary>Invalid implementation.</summary>
    InvalidImplementation = 25,

    /// <summary>Incompatible old storage.</summary>
    IncompatibleOldStorage = 26,

    /// <summary>Incompatible new storage.</summary>
    IncompatibleNewStorage = 27,

    /// <summary>Incorrect context for data element ID.</summary>
    IncorrectContextForDataElementID = 28,

    /// <summary>Object group duplicate objects.</summary>
    ObjectGroupDuplicateObjects = 29,

    /// <summary>Object reference not found in revision.</summary>
    ObjectReferenceNotFoundInRevision = 31,

    /// <summary>Merge cell storage state conflict.</summary>
    MergeCellStorageStateConflict = 32,

    /// <summary>Unknown query changes filter.</summary>
    UnknownQueryChangesFilter = 33,

    /// <summary>Unsupported query changes filter.</summary>
    UnsupportedQueryChangesFilter = 34,

    /// <summary>Unable to provide knowledge.</summary>
    UnableToProvideKnowledge = 35,
}

/// <summary>
/// The protocol error code enumeration.
/// </summary>
public enum ProtocolErrorCode
{
    /// <summary>Unknown error.</summary>
    UnknownError = 1,

    /// <summary>Invalid object.</summary>
    InvalidObject = 2,

    /// <summary>Invalid partition.</summary>
    InvalidPartition = 3,

    /// <summary>Request not supported.</summary>
    RequestNotSupported = 4,

    /// <summary>Storage read-only.</summary>
    StorageReadOnly = 5,

    /// <summary>Revision ID not found.</summary>
    RevisionIDNotFound = 6,

    /// <summary>Bad token.</summary>
    BadToken = 7,

    /// <summary>Request not finished.</summary>
    RequestNotFinished = 8,

    /// <summary>Incompatible token.</summary>
    IncompatibleToken = 9,

    /// <summary>Scoped cell storage.</summary>
    ScopedCellStorage = 11,

    /// <summary>Coherency failure.</summary>
    CoherencyFailure = 12,

    /// <summary>Cell storage state deserialization failure.</summary>
    CellStorageStateDeserializationFailure = 13,

    /// <summary>Incompatible protocol version.</summary>
    IncompatibleProtocolVersion = 15,

    /// <summary>Referenced data element not found.</summary>
    ReferencedDataElementNotFound = 16,

    /// <summary>Request stream schema error.</summary>
    RequestStreamSchemaError = 18,

    /// <summary>Response stream schema error.</summary>
    ResponseStreamSchemaError = 19,

    /// <summary>Unknown request.</summary>
    UnknownRequest = 20,

    /// <summary>Storage failure.</summary>
    StorageFailure = 21,

    /// <summary>Storage write only.</summary>
    StorageWriteOnly = 22,

    /// <summary>Invalid serialization.</summary>
    InvalidSerialization = 23,

    /// <summary>Data element not found.</summary>
    DataElementNotFound = 24,

    /// <summary>Invalid implementation.</summary>
    InvalidImplementation = 25,

    /// <summary>Incompatible old storage.</summary>
    IncompatibleOldStorage = 26,

    /// <summary>Incompatible new storage.</summary>
    IncompatibleNewStorage = 27,

    /// <summary>Incorrect context for data element ID.</summary>
    IncorrectContextForDataElementID = 28,

    /// <summary>Object group duplicate objects.</summary>
    ObjectGroupDuplicateObjects = 29,

    /// <summary>Object reference not found in revision.</summary>
    ObjectReferenceNotFoundInRevision = 31,

    /// <summary>Merge cell storage state conflict.</summary>
    MergeCellStorageStateConflict = 32,

    /// <summary>Unknown query changes filter.</summary>
    UnknownQueryChangesFilter = 33,

    /// <summary>Unsupported query changes filter.</summary>
    UnsupportedQueryChangesFilter = 34,

    /// <summary>Unable to provide knowledge.</summary>
    UnableToProvideKnowledge = 35,
}
