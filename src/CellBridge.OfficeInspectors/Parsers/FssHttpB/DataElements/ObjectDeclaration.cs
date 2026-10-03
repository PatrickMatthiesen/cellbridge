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
    /// 2.2.1.12.6.1	Object Declaration
    /// </summary>
    public class ObjectDeclaration : BaseStructure
    {
        public StreamObjectHeader ObjectGroupObjectDeclaration;
        public ExtendedGUID ObjectExtendedGUID;
        public CompactUnsigned64bitInteger ObjectPartitionID;
        public CompactUnsigned64bitInteger ObjectDataSize;
        public CompactUnsigned64bitInteger ObjectReferencesCount;
        public CompactUnsigned64bitInteger CellReferencesCount;

        /// <summary>
        /// Parse the ObjectDeclaration structure.
        /// </summary>
        /// <param name="s">A stream containing ObjectDeclaration structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.ObjectGroupObjectDeclaration = new StreamObjectHeader();
            this.ObjectGroupObjectDeclaration = this.ObjectGroupObjectDeclaration.TryParse(s);
            this.ObjectExtendedGUID = new ExtendedGUID();
            this.ObjectExtendedGUID = this.ObjectExtendedGUID.TryParse(s);
            this.ObjectPartitionID = new CompactUnsigned64bitInteger();
            this.ObjectPartitionID = this.ObjectPartitionID.TryParse(s);
            this.ObjectDataSize = new CompactUnsigned64bitInteger();
            this.ObjectDataSize = this.ObjectDataSize.TryParse(s);
            this.ObjectReferencesCount = new CompactUnsigned64bitInteger();
            this.ObjectReferencesCount = this.ObjectReferencesCount.TryParse(s);
            this.CellReferencesCount = new CompactUnsigned64bitInteger();
            this.CellReferencesCount = this.CellReferencesCount.TryParse(s);
        }
    }
}
