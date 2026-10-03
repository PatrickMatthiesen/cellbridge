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
    /// The enumeration of the stream object type header end
    /// </summary>
    public enum StreamObjectTypeHeaderEnd
    {
        DataElement = 0x01,
        Knowledge = 0x10,
        CellKnowledge = 0x14,
        DataElementPackage = 0x15,
        ObjectGroupDeclarations = 0x1D,
        ObjectGroupData = 0x1E,
        LeafNodeEnd = 0x1F, // Defined in MS-FSSHTTPD
        IntermediateNodeEnd = 0x20, // Defined in MS-FSSHTTPD
        WaterlineKnowledge = 0x29,
        ContentTagKnowledge = 0x2D,
        Request = 0x040,
        SubResponse = 0x041,
        SubRequest = 0x042,
        ReadAccessResponse = 0x043,
        SpecializedKnowledge = 0x044,
        WriteAccessResponse = 0x046,
        QueryChangesFilter = 0x047,
        Error = 0x04D,
        QueryChangesRequest = 0x051,
        UserAgent = 0x05D,
        Response = 0x062,
        FragmentKnowledge = 0x06B,
        ObjectGroupMetadataDeclarations = 0x79,
        TargetPartitionId = 0x083
    }
}
