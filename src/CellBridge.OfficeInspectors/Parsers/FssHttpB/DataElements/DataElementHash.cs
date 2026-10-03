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
    /// 2.2.1.12.6.6	Data Element Hash
    /// </summary>
    public class DataElementHash : BaseStructure
    {
        public StreamObjectHeader DataElementHashDeclaration;
        public CompactUnsigned64bitInteger DataElementHashScheme;
        public BinaryItem DataElementHashData;

        /// <summary>
        /// Parse the DataElementHash structure.
        /// </summary>
        /// <param name="s">A stream containing DataElementHash structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.DataElementHashDeclaration = new StreamObjectHeader();
            this.DataElementHashDeclaration = this.DataElementHashDeclaration.TryParse(s);
            this.DataElementHashScheme = new CompactUnsigned64bitInteger();
            this.DataElementHashScheme = this.DataElementHashScheme.TryParse(s);
            this.DataElementHashData = new BinaryItem();
            this.DataElementHashData.Parse(s);
        }
    }
}
