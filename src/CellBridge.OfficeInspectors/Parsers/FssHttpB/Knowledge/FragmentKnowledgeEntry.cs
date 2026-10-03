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
    /// 2.2.1.13.3.1	Fragment Knowledge Entry
    /// </summary>
    public class FragmentKnowledgeEntry : BaseStructure
    {
        public bit32StreamObjectHeaderStart FragmentDescriptor;
        public ExtendedGUID ExtendedGUID;
        public CompactUnsigned64bitInteger DataElementSize;
        public FileChunkReference DataElementChunkReference;

        /// <summary>
        /// Parse the FragmentKnowledgeEntry structure.
        /// </summary>
        /// <param name="s">A stream containing FragmentKnowledgeEntry structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.FragmentDescriptor = new bit32StreamObjectHeaderStart();
            this.FragmentDescriptor.Parse(s);
            this.ExtendedGUID = new ExtendedGUID();
            this.ExtendedGUID = this.ExtendedGUID.TryParse(s);

            this.DataElementSize = new CompactUnsigned64bitInteger();
            this.DataElementSize = this.DataElementSize.TryParse(s);
            this.DataElementChunkReference = new FileChunkReference();
            this.DataElementChunkReference.Parse(s);
        }
    }
}
