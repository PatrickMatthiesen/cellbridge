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
    /// 2.2.1.13.2.1   Cell Knowledge Range
    /// </summary>
    public class CellKnowledgeRange : BaseStructure
    {
        public bit16StreamObjectHeaderStart cellKnowledgeRange;
        public Guid GUID;
        public CompactUnsigned64bitInteger From;
        public CompactUnsigned64bitInteger To;

        /// <summary>
        /// Parse the CellKnowledgeRange structure.
        /// </summary>
        /// <param name="s">A stream containing CellKnowledgeRange structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.cellKnowledgeRange = new bit16StreamObjectHeaderStart();
            this.cellKnowledgeRange.Parse(s);
            this.GUID = ReadGuid();
            this.From = new CompactUnsigned64bitInteger();
            this.From = this.From.TryParse(s);
            this.To = new CompactUnsigned64bitInteger();
            this.To = this.To.TryParse(s);
        }
    }
}
