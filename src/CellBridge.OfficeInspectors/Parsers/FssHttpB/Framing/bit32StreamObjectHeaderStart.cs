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
    /// 2.2.1.5.2	32-bit Stream Object Header Start
    /// </summary>
    public class bit32StreamObjectHeaderStart : StreamObjectHeader
    {
        [BitAttribute(2)]
        public byte A;
        [BitAttribute(1)]
        public byte B;
        [BitAttribute(14)]
        public StreamObjectTypeHeaderStart Type;
        [BitAttribute(15)]
        public short Length;
        public CompactUnsigned64bitInteger LargeLength;

        /// <summary>
        /// Parse the bit32StreamObjectHeaderStart structure.
        /// </summary>
        /// <param name="s">A stream containing bit32StreamObjectHeaderStart structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            int index = 0;
            uint temp = ReadUint();
            if ((temp & 3) != 2)
                throw new InvalidDataException("Invalid bit32StreamObjectHeaderStart discriminator.");
            this.A = (byte)GetBits(temp, index, 2);
            index = index + 2;
            this.B = (byte)GetBits(temp, index, 1);
            index = index + 1;
            this.Type = (StreamObjectTypeHeaderStart)GetBits(temp, index, 14);
            index = index + 14;
            this.Length = (short)GetBits(temp, index, 15);

            if (this.Length == 32767)
            {
                this.LargeLength = new CompactUnsigned64bitInteger();
                this.LargeLength = this.LargeLength.TryParse(s);
            }
        }
        /// <summary>
        /// Get the Data length of the data that with bit32StreamObjectHeaderStart
        /// </summary>
        /// <returns>the length of data</returns>
        public int GetDataLength()
        {
            if (this.Length != 32767)
                return (int)this.Length;
            else
                return checked((int)this.LargeLength.GetUint(this.LargeLength));
        }
    }
}
