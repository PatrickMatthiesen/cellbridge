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
    /// 2.2.1.12.4	Cell Manifest Data Element
    /// </summary>
    public class CellManifestDataElement : BaseStructure
    {
        public StreamObjectHeader DataElementStart;
        public ExtendedGUID DataElementExtendedGUID;
        public SerialNumber SerialNumber;
        public CompactUnsigned64bitInteger DataElementType;
        public bit16StreamObjectHeaderStart CellManifestCurrentRevision;
        public ExtendedGUID CellManifestCurrentRevisionExtendedGUID;
        public bit8StreamObjectHeaderEnd DataElementEnd;

        /// <summary>
        /// Parse the CellManifestDataElement structure.
        /// </summary>
        /// <param name="s">A stream containing CellManifestDataElement structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.DataElementStart = new StreamObjectHeader().TryParse(s);
            this.DataElementExtendedGUID = new ExtendedGUID();
            this.DataElementExtendedGUID = this.DataElementExtendedGUID.TryParse(s);
            this.SerialNumber = new SerialNumber();
            this.SerialNumber = this.SerialNumber.TryParse(s);
            this.DataElementType = new CompactUnsigned64bitInteger();
            this.DataElementType = this.DataElementType.TryParse(s);
            this.CellManifestCurrentRevision = new bit16StreamObjectHeaderStart();
            this.CellManifestCurrentRevision.Parse(s);
            this.CellManifestCurrentRevisionExtendedGUID = new ExtendedGUID();
            this.CellManifestCurrentRevisionExtendedGUID = this.CellManifestCurrentRevisionExtendedGUID.TryParse(s);
            this.DataElementEnd = new bit8StreamObjectHeaderEnd();
            this.DataElementEnd.Parse(s);
            if (this.DataElementEnd.Type != StreamObjectTypeHeaderEnd.DataElement)
                throw new InvalidDataException("Expected DataElement end header.");
        }
    }
}
