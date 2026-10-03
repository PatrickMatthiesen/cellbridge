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
    /// This type is defined for section 2.2.1.12.2 Storage Index Data Element of cell mapping values
    /// </summary>
    public class StorageIndexCellMappingValues : BaseStructure
    {
        public bit16StreamObjectHeaderStart StorageIndexCellMapping;
        public CellID CellID;
        public ExtendedGUID CellMappingExtendedGUID;
        public SerialNumber CellMappingSerialNumber;

        /// <summary>
        /// Parse the StorageIndexCellMappingValues structure.
        /// </summary>
        /// <param name="s">A stream containing StorageIndexCellMappingValues structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.StorageIndexCellMapping = new bit16StreamObjectHeaderStart();
            this.StorageIndexCellMapping.Parse(s);
            this.CellID = new CellID();
            this.CellID.Parse(s);
            this.CellMappingExtendedGUID = new ExtendedGUID();
            this.CellMappingExtendedGUID = this.CellMappingExtendedGUID.TryParse(s);
            this.CellMappingSerialNumber = new SerialNumber();
            this.CellMappingSerialNumber = this.CellMappingSerialNumber.TryParse(s);
        }
    }
}
