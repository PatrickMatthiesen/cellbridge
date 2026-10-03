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
    /// 2.2.1.13	Knowledge
    /// </summary>
    public class Knowledge : BaseStructure
    {
        public bit16StreamObjectHeaderStart KnowledgeStart;
        public SpecializedKnowledge[] SpecializedKnowledge;
        public bit8StreamObjectHeaderEnd KnowledgeEnd;

        /// <summary>
        /// Parse the Knowledge structure.
        /// </summary>
        /// <param name="s">A stream containing Knowledge structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.KnowledgeStart = new bit16StreamObjectHeaderStart();
            this.KnowledgeStart.Parse(s);

            List<SpecializedKnowledge> tempSpecializedKnowledge = new List<SpecializedKnowledge>();
            while (ContainsStreamObjectStart32BitHeader(0x44))
            {
                SpecializedKnowledge knowledge = new SpecializedKnowledge();
                knowledge.Parse(s);
                tempSpecializedKnowledge.Add(knowledge);
            };
            this.SpecializedKnowledge = tempSpecializedKnowledge.ToArray();
            this.KnowledgeEnd = new bit8StreamObjectHeaderEnd();
            this.KnowledgeEnd.Parse(s);
            if (this.KnowledgeEnd.Type != StreamObjectTypeHeaderEnd.Knowledge)
                throw new InvalidDataException("Expected Knowledge end header.");
        }
    }
}
