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
    /// 2.2.1.13.2	Cell Knowledge
    /// </summary>
    public class CellKnowLedge : BaseStructure
    {
        public bit16StreamObjectHeaderStart CellKnowledgeStart;
        public object[] CellKnowledgeData;
        public bit8StreamObjectHeaderEnd CellKnowledgeEnd;

        /// <summary>
        /// Parse the CellKnowLedge structure.
        /// </summary>
        /// <param name="s">A stream containing CellKnowLedge structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.CellKnowledgeStart = new bit16StreamObjectHeaderStart();
            this.CellKnowledgeStart.Parse(s);
            List<object> tempCell = new List<object>();
            while (ContainsStreamObjectStart16BitHeader(0x0F) || ContainsStreamObjectStart16BitHeader(0x17))
            {
                if (ContainsStreamObjectStart16BitHeader(0x0F))
                {
                    CellKnowledgeRange cellknowledge = new CellKnowledgeRange();
                    cellknowledge.Parse(s);
                    tempCell.Add(cellknowledge);
                }
                else if (ContainsStreamObjectStart16BitHeader(0x17))
                {
                    CellKnowledgeEntry cellknowledge = new CellKnowledgeEntry();
                    cellknowledge.Parse(s);
                    tempCell.Add(cellknowledge);
                }
            }
            this.CellKnowledgeData = tempCell.ToArray();

            this.CellKnowledgeEnd = new bit8StreamObjectHeaderEnd();
            this.CellKnowledgeEnd.Parse(s);
            if (this.CellKnowledgeEnd.Type != StreamObjectTypeHeaderEnd.CellKnowledge)
                throw new InvalidDataException("Expected CellKnowledge end header.");
        }
    }
}
