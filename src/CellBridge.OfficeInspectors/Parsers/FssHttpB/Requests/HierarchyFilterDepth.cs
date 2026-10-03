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
    /// The enumeration of the hierarchy filter depth
    /// </summary>
    public enum HierarchyFilterDepth : byte
    {
        // Index values corresponding to the specified keys only.
        IndexOnly = 0,

        // First data elements referenced by the storage index values corresponding to the specified keys only.
        FirstDataElement = 1,

        // Single level. All data elements under the sub-graphs rooted by the specified keys stopping at any storage index entries.
        SingleLevel = 2,

        // Deep. All data elements and storage index entries under the sub-graphs rooted by the specified keys.
        Deep = 3
    }
}
