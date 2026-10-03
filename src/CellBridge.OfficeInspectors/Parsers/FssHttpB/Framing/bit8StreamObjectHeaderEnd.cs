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
    /// 2.2.1.5.3	8-bit Stream Object Header End
    /// </summary>
    public class bit8StreamObjectHeaderEnd : StreamObjectHeader
    {
        [BitAttribute(2)]
        public byte A;
        [BitAttribute(6)]
        public StreamObjectTypeHeaderEnd Type;

        /// <summary>
        /// Parse the bit8StreamObjectHeaderEnd structure.
        /// </summary>
        /// <param name="s">A stream containing bit8StreamObjectHeaderEnd structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            byte temp = ReadByte();
            this.A = (byte)(temp & 3);
            if (this.A is not (1 or 3))
                throw new InvalidDataException("Expected a stream-object end header.");
            int value = this.A == 3 ? temp | (ReadByte() << 8) : temp;
            this.Type = (StreamObjectTypeHeaderEnd)(value >> 2);
        }
    }
}
