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
    /// 2.2.2.1.4.1	Additional Flags
    /// </summary>
    public class AdditionalFlags : BaseStructure
    {
        public bit32StreamObjectHeaderStart AdditionalFlagsHeader;
        [BitAttribute(1)]
        public byte A;
        [BitAttribute(1)]
        public byte B;
        [BitAttribute(1)]
        public byte C;
        [BitAttribute(1)]
        public byte D;
        [BitAttribute(1)]
        public byte E;
        [BitAttribute(1)]
        public byte F;
        [BitAttribute(10)]
        public ushort Reserved;
        public CompactUnsigned64bitInteger Reserved2;

        /// <summary>
        /// Parse the AdditionalFlags structure.
        /// </summary>
        /// <param name="s">A stream containing AdditionalFlags structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.AdditionalFlagsHeader = new bit32StreamObjectHeaderStart();
            this.AdditionalFlagsHeader.Parse(s);
            short tempUshort = ReadINT16();
            this.A = (byte)GetBits(tempUshort, 0, 1);
            this.B = (byte)GetBits(tempUshort, 1, 1);
            this.C = (byte)GetBits(tempUshort, 2, 1);
            this.D = (byte)GetBits(tempUshort, 3, 1);
            this.E = (byte)GetBits(tempUshort, 4, 1);
            this.F = (byte)GetBits(tempUshort, 5, 1);
            this.Reserved = (byte)GetBits(tempUshort, 6, 16);

            this.Reserved2 = new CompactUnsigned64bitInteger();
            this.Reserved2 = this.Reserved2.TryParse(s);
        }
    }
}
