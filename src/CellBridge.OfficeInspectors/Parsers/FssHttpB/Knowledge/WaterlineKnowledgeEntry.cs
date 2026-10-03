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
    /// 2.2.1.13.4.1	Waterline Knowledge Entry
    /// </summary>
    public class WaterlineKnowledgeEntry : BaseStructure
    {
        public bit16StreamObjectHeaderStart waterlineKnowledgeEntry;
        public ExtendedGUID CellStorageExtendedGUID;
        public CompactUnsigned64bitInteger Waterline;
        public CompactUnsigned64bitInteger Reserved;

        /// <summary>
        /// Parse the WaterlineKnowledgeEntry structure.
        /// </summary>
        /// <param name="s">A stream containing WaterlineKnowledgeEntry structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.waterlineKnowledgeEntry = new bit16StreamObjectHeaderStart();
            this.waterlineKnowledgeEntry.Parse(s);
            this.CellStorageExtendedGUID = new ExtendedGUID();
            this.CellStorageExtendedGUID = this.CellStorageExtendedGUID.TryParse(s);

            this.Waterline = new CompactUnsigned64bitInteger();
            this.Waterline = this.Waterline.TryParse(s);
            this.Reserved = new CompactUnsigned64bitInteger();
            this.Reserved = this.Reserved.TryParse(s);
        }
    }
}
