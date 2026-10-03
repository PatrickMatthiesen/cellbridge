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
    /// 2.2.1.13.4	Waterline Knowledge
    /// </summary>
    public class WaterlineKnowledge : BaseStructure
    {
        public bit16StreamObjectHeaderStart WaterlineKnowledgeStart;
        public WaterlineKnowledgeEntry[] WaterlineKnowledgeData;
        public bit8StreamObjectHeaderEnd WaterlineKnowledgeEnd;

        /// <summary>
        /// Parse the WaterlineKnowledge structure.
        /// </summary>
        /// <param name="s">A stream containing WaterlineKnowledge structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.WaterlineKnowledgeStart = new bit16StreamObjectHeaderStart();
            this.WaterlineKnowledgeStart.Parse(s);
            List<WaterlineKnowledgeEntry> tempWaterline = new List<WaterlineKnowledgeEntry>();
            do
            {
                WaterlineKnowledgeEntry Waterlineknowledge = new WaterlineKnowledgeEntry();
                Waterlineknowledge.Parse(s);
                tempWaterline.Add(Waterlineknowledge);
            } while (ContainsStreamObjectStart16BitHeader(0x04));
            this.WaterlineKnowledgeData = tempWaterline.ToArray();
            this.WaterlineKnowledgeEnd = new bit8StreamObjectHeaderEnd();
            this.WaterlineKnowledgeEnd.Parse(s);
            if (this.WaterlineKnowledgeEnd.Type != StreamObjectTypeHeaderEnd.WaterlineKnowledge)
                throw new InvalidDataException("Expected WaterlineKnowledge end header.");
        }
    }
}
