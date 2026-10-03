#nullable disable
// Ported from OfficeDev/Office-Inspectors-for-Fiddler. See NOTICE.md in this project.
//-----------------------------------------------------------------------
// Copyright (c) 2013 Microsoft Corporation. All rights reserved.
// Use of this sample source code is subject to the terms of the Microsoft license
// agreement under which you licensed this sample source code and is provided AS-IS.
// If you did not accept the terms of the license agreement, you are not authorized
// to use this sample source code. For the terms of the license, please see the
// license agreement between you and Microsoft.
//-----------------------------------------------------------------------

namespace CellBridge.OfficeInspectors.Parsers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Xml.Serialization;
    using System.Xml;
    using System.Xml.Schema;
    using System.Reflection;
    using System.Linq;

    /// <summary>
    /// The enumeration of the stream object type header start
    /// </summary>
    public enum StreamObjectTypeHeaderStart
    {
        Unknown = 0x4E,
        DataElement = 0x01,
        ObjectDataBLOB = 0x02,
        ObjectGroupObjectExcludedData = 0x03,
        WaterlineKnowledgeEntry = 0x04,
        ObjectGroupObjectBLOBDataDeclaration = 0x05,
        DataElementHash = 0x06,
        StorageManifestRootDeclare = 0x07,
        RevisionManifestRootDeclare = 0x0A,
        CellManifestCurrentRevision = 0x0B,
        StorageManifestSchemaGUID = 0x0C,
        StorageIndexRevisionMapping = 0x0D,
        StorageIndexCellMapping = 0x0E,
        CellKnowledgeRange = 0x0F,
        Knowledge = 0x10,
        StorageIndexManifestMapping = 0x11,
        CellKnowledge = 0x14,
        DataElementPackage = 0x15,
        ObjectGroupObjectData = 0x16,
        CellKnowledgeEntry = 0x17,
        ObjectGroupObjectDeclare = 0x18,
        RevisionManifestObjectGroupReferences = 0x19,
        RevisionManifest = 0x1A,
        ObjectGroupObjectDataBLOBReference = 0x1C,
        ObjectGroupDeclarations = 0x1D,
        ObjectGroupData = 0x1E,
        LeafNodeObject = 0x1F, // Defined in MS-FSSHTTPD
        IntermediateNodeObject = 0x20, // Defined in MS-FSSHTTPD
        SignatureObject = 0x21, // Defined in MS-FSSHTTPD
        DataSizeObject = 0x22, // Defined in MS-FSSHTTPD
        WaterlineKnowledge = 0x29,
        ContentTagKnowledge = 0x2D,
        ContentTagKnowledgeEntry = 0x2E,
        QueryChangesVersioning = 0x30,
        Request = 0x040,
        FsshttpbSubResponse = 0x041,
        SubRequest = 0x042,
        ReadAccessResponse = 0x043,
        SpecializedKnowledge = 0x044,
        PutChangesResponseSerialNumberReassignAll = 0x045,
        WriteAccessResponse = 0x046,
        QueryChangesFilter = 0x047,
        Win32Error = 0x049,
        ProtocolError = 0x04B,
        ResponseError = 0x04D,
        UserAgentversion = 0x04F,
        QueryChangesFilterSchemaSpecific = 0x050,
        QueryChangesRequest = 0x051,
        HRESULTError = 0x052,
        PutChangesResponseSerialNumberReassign = 0x053,
        QueryChangesFilterDataElementIDs = 0x054,
        UserAgentGUID = 0x055,
        QueryChangesFilterDataElementType = 0x057,
        QueryChangesDataConstraint = 0x059,
        PutChangesRequest = 0x05A,
        QueryChangesRequestArguments = 0x05B,
        QueryChangesFilterCellID = 0x05C,
        UserAgent = 0x05D,
        QueryChangesResponse = 0x05F,
        QueryChangesFilterHierarchy = 0x060,
        FsshttpbResponse = 0x062,
        QueryDataElementRequest = 0x065,
        CellError = 0x066,
        QueryChangesFilterFlags = 0x068,
        DataElementFragment = 0x06A,
        FragmentKnowledge = 0x06B,
        FragmentKnowledgeEntry = 0x06C,
        ObjectGroupMetadataDeclarations = 0x79,
        ObjectGroupMetadata = 0x78,
        AllocateExtendedGUIDRangeRequest = 0x080,
        AllocateExtendedGUIDRangeResponse = 0x081,
        TargetPartitionId = 0x083,
        PutChangesLockId = 0x085,
        AdditionalFlags = 0x086,
        PutChangesResponse = 0x087,
        RequestHashOptions = 0x088,
        DiagnosticRequestOptionOutput = 0x089,
        DiagnosticRequestOptionInput = 0x08A,
        UserAgentClientandPlatform = 0x08B,
        VersionTokenKnowledge = 0x08C,
        CellRoundtripOptions = 0x08C,
    }
}
