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
    /// 2.2.1.13.5	Content Tag Knowledge
    /// </summary>
    public class ContentTagKnowledge : BaseStructure
    {
        public bit16StreamObjectHeaderStart ContentTagKnowledgeStart;
        public ContentTagKnowledgeEntry[] ContentTagKnowledgeEntryArray;
        public bit8StreamObjectHeaderEnd ContentTagKnowledgeEnd;

        /// <summary>
        /// Parse the WaterlineKnowledge structure.
        /// </summary>
        /// <param name="s">A stream containing WaterlineKnowledge structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.ContentTagKnowledgeStart = new bit16StreamObjectHeaderStart();
            this.ContentTagKnowledgeStart.Parse(s);
            List<ContentTagKnowledgeEntry> tempContentTag = new List<ContentTagKnowledgeEntry>();
            while (ContainsStreamObjectHeader(0x2E))
            {
                ContentTagKnowledgeEntry ContentTagknowledge = new ContentTagKnowledgeEntry();
                ContentTagknowledge.Parse(s);
                tempContentTag.Add(ContentTagknowledge);
            };
            this.ContentTagKnowledgeEntryArray = tempContentTag.ToArray();
            this.ContentTagKnowledgeEnd = new bit8StreamObjectHeaderEnd();
            this.ContentTagKnowledgeEnd.Parse(s);
            if (this.ContentTagKnowledgeEnd.Type != StreamObjectTypeHeaderEnd.ContentTagKnowledge)
                throw new InvalidDataException("Expected ContentTagKnowledge end header.");
        }
    }
}
