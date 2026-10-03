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
    /// 2.2.1.13.3	Fragment Knowledge
    /// </summary>
    public class FragmentKnowledge : BaseStructure
    {
        public bit32StreamObjectHeaderStart FragmentKnowledgeStart;
        public FragmentKnowledgeEntry[] FragmentKnowledgeEntries;
        public bit16StreamObjectHeaderEnd FragmentKnowledgeEnd;

        /// <summary>
        /// Parse the FragmentKnowledge structure.
        /// </summary>
        /// <param name="s">A stream containing FragmentKnowledge structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.FragmentKnowledgeStart = new bit32StreamObjectHeaderStart();
            this.FragmentKnowledgeStart.Parse(s);
            List<FragmentKnowledgeEntry> tempFragment = new List<FragmentKnowledgeEntry>();
            while (ContainsStreamObjectStart32BitHeader(0x06C))
            {
                FragmentKnowledgeEntry Fragmentknowledge = new FragmentKnowledgeEntry();
                Fragmentknowledge.Parse(s);
                tempFragment.Add(Fragmentknowledge);
            };
            this.FragmentKnowledgeEntries = tempFragment.ToArray();
            this.FragmentKnowledgeEnd = new bit16StreamObjectHeaderEnd();
            this.FragmentKnowledgeEnd.Parse(s);
            if (this.FragmentKnowledgeEnd.Type != StreamObjectTypeHeaderEnd.FragmentKnowledge)
                throw new InvalidDataException("Expected FragmentKnowledge end header.");
        }
    }
}
