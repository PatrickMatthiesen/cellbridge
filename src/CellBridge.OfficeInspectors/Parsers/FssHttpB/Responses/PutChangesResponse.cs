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
    /// 2.2.3.1.3	Put Changes
    /// </summary>
    public class PutChangesResponse : BaseStructure
    {
        public PutChangesSerialNumberReassignment SerialNumberReassignAll;
        public PutChangesSerialNumberReassignment[] SerialNumberReassignments = [];
        public bit32StreamObjectHeaderStart putChangesResponse;
        public ExtendedGUID AppliedStorageIndexId;
        public ExtendedGUIDArray DataElementsAdded;
        public Knowledge ResultantKnowledge;
        public DiagnosticRequesOptionOutput DiagnosticRequestOptionOutput;

        /// <summary>
        /// Parse the PutChangesResponse structure.
        /// </summary>
        /// <param name="s">A stream containing PutChangesResponse structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            // OfficeDev's test-suite grammar and desktop Word accept these
            // reassignment records before the applied index and Knowledge.
            if (ContainsStreamObjectStart32BitHeader(0x045))
            {
                this.SerialNumberReassignAll = new PutChangesSerialNumberReassignment();
                this.SerialNumberReassignAll.Parse(s);
                var entries = new List<PutChangesSerialNumberReassignment>();
                while (ContainsStreamObjectStart32BitHeader(0x053))
                {
                    var entry = new PutChangesSerialNumberReassignment();
                    entry.Parse(s);
                    entries.Add(entry);
                }
                this.SerialNumberReassignments = entries.ToArray();
            }
            if (ContainsStreamObjectStart32BitHeader(0x87))
            {
                this.putChangesResponse = new bit32StreamObjectHeaderStart();
                this.putChangesResponse.Parse(s);
                this.AppliedStorageIndexId = new ExtendedGUID();
                this.AppliedStorageIndexId = this.AppliedStorageIndexId.TryParse(s);
                this.DataElementsAdded = new ExtendedGUIDArray();
                this.DataElementsAdded.Parse(s);
            }

            if (!ContainsStreamObjectStart16BitHeader(0x10))
                throw new InvalidDataException("Put Changes response is missing its mandatory Knowledge object.");
            this.ResultantKnowledge = new Knowledge();
            this.ResultantKnowledge.Parse(s);
            if (ContainsStreamObjectStart32BitHeader(0x89))
            {
                this.DiagnosticRequestOptionOutput = new DiagnosticRequesOptionOutput();
                this.DiagnosticRequestOptionOutput.Parse(s);
            }
        }
    }
}
