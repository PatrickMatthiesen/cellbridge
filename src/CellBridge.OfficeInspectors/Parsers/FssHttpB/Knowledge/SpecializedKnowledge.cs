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
    /// 2.2.1.13.1	Specialized Knowledge
    /// </summary>
    public class SpecializedKnowledge : BaseStructure
    {
        public bit32StreamObjectHeaderStart SpecializedKnowledgeStart;
        public Guid GUID;
        public object SpecializedKnowledgeData;
        public bit16StreamObjectHeaderEnd SpecializedKnowledgeEnd;

        /// <summary>
        /// Parse the SpecializedKnowledge structure.
        /// </summary>
        /// <param name="s">A stream containing SpecializedKnowledge structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.SpecializedKnowledgeStart = new bit32StreamObjectHeaderStart();
            this.SpecializedKnowledgeStart.Parse(s);
            this.GUID = ReadGuid();

            switch (this.GUID.ToString().ToUpper())
            {
                case "327A35F6-0761-4414-9686-51E900667A4D":
                    this.SpecializedKnowledgeData = new CellKnowLedge();
                    ((CellKnowLedge)this.SpecializedKnowledgeData).Parse(s);
                    break;
                case "3A76E90E-8032-4D0C-B9DD-F3C65029433E":
                    this.SpecializedKnowledgeData = new WaterlineKnowledge();
                    ((WaterlineKnowledge)this.SpecializedKnowledgeData).Parse(s);
                    break;
                case "0ABE4F35-01DF-4134-A24A-7C79F0859844":
                    this.SpecializedKnowledgeData = new FragmentKnowledge();
                    ((FragmentKnowledge)this.SpecializedKnowledgeData).Parse(s);
                    break;
                case "10091F13-C882-40FB-9886-6533F934C21D":
                    this.SpecializedKnowledgeData = new ContentTagKnowledge();
                    ((ContentTagKnowledge)this.SpecializedKnowledgeData).Parse(s);
                    break;
                case "BF12E2C1-E64F-4959-8282-73B9A24A7C44":
                    this.SpecializedKnowledgeData = new VersionTokenKnowledge();
                    ((VersionTokenKnowledge)this.SpecializedKnowledgeData).Parse(s);
                    break;
                default:
                    throw new Exception("The GUID is not right.");

            }
            this.SpecializedKnowledgeEnd = new bit16StreamObjectHeaderEnd();
            this.SpecializedKnowledgeEnd.Parse(s);
            if (this.SpecializedKnowledgeEnd.Type != StreamObjectTypeHeaderEnd.SpecializedKnowledge)
                throw new InvalidDataException("Expected SpecializedKnowledge end header.");
        }
    }
}
