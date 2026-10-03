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
    /// 2.2.1.5.4	16-bit Stream Object Header End
    /// </summary>
    public class bit16StreamObjectHeaderEnd : StreamObjectHeader
    {
        [BitAttribute(2)]
        public byte A;
        [BitAttribute(14)]
        public StreamObjectTypeHeaderEnd Type;

        /// <summary>
        /// Parse the bit16StreamObjectHeaderEnd structure.
        /// </summary>
        /// <param name="s">A stream containing bit16StreamObjectHeaderEnd structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            int index = 0;
            int temp = ReadUshort();
            if ((temp & 3) != 3)
                throw new InvalidDataException("Invalid bit16StreamObjectHeaderEnd discriminator.");
            this.A = (byte)GetBits(temp, index, 2);
            index = index + 2;
            this.Type = (StreamObjectTypeHeaderEnd)GetBits(temp, index, 14);
        }
    }
}
