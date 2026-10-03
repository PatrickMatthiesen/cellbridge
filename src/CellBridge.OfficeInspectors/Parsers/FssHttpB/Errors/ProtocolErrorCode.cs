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
    /// The enumeration of the protocol error code, section 2.2.3.2.2
    /// </summary>
    public enum ProtocolErrorCode
    {
        /// <summary>
        /// Unknown error
        /// </summary>
        Unknownerror = 1,

        /// <summary>
        /// End of Stream
        /// </summary>
        EndofStream = 50,

        /// <summary>
        /// Unknown internal error
        /// </summary>
        Unknowninternalerror = 61,

        /// <summary>
        /// Input stream schema invalid
        /// </summary>
        Inputstreamschemainvalid = 108,

        /// <summary>
        /// Stream object invalid
        /// </summary>
        Streamobjectinvalid = 142,

        /// <summary>
        /// Stream object unexpected
        /// </summary>
        Streamobjectunexpected = 143,

        /// <summary>
        /// Server URL not found
        /// </summary>
        ServerURLnotfound = 144,

        /// <summary>
        /// Stream object serialization error
        /// </summary>
        Streamobjectserializationerror = 145,
    }
}
