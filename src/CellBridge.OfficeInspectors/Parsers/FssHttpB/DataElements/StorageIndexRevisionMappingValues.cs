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
    /// This type is defined for section 2.2.1.12.2 Storage Index Data Element of revising mapping values
    /// </summary>
    public class StorageIndexRevisionMappingValues : BaseStructure
    {
        public bit16StreamObjectHeaderStart StorageIndexRevisionMapping;
        public ExtendedGUID RevisionExtendedGUID;
        public ExtendedGUID RevisionMappingExtendedGUID;
        public SerialNumber RevisionMappingSerialNumber;

        /// <summary>
        /// Parse the StorageIndexRevisionMappingValues structure.
        /// </summary>
        /// <param name="s">A stream containing StorageIndexRevisionMappingValues structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.StorageIndexRevisionMapping = new bit16StreamObjectHeaderStart();
            this.StorageIndexRevisionMapping.Parse(s);
            this.RevisionExtendedGUID = new ExtendedGUID();
            this.RevisionExtendedGUID = this.RevisionExtendedGUID.TryParse(s);
            this.RevisionMappingExtendedGUID = new ExtendedGUID();
            this.RevisionMappingExtendedGUID = this.RevisionMappingExtendedGUID.TryParse(s);
            this.RevisionMappingSerialNumber = new SerialNumber();
            this.RevisionMappingSerialNumber = this.RevisionMappingSerialNumber.TryParse(s);
        }
    }
}
