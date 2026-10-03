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
    /// 2.2.1.7.2	Extended GUID 5 Bit Uint Value
    /// </summary>
    public class ExtendedGUID5BitUintValue : ExtendedGUID
    {
        [BitAttribute(3)]
        public byte Type;
        [BitAttribute(5)]
        public byte Value;
        public Guid GUID;

        /// <summary>
        /// Parse the ExtendedGUID5BitUintValue structure.
        /// </summary>
        /// <param name="s">A stream containing ExtendedGUID5BitUintValue structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            byte tempByte = ReadByte();
            int index = 0;
            this.Type = GetBits(tempByte, index, 3);
            index += 3;
            this.Value = GetBits(tempByte, index, 5);
            this.GUID = ReadGuid();
        }
    }
}
