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
    /// 2.2.1.7.3	Extended GUID 10 Bit Uint Value
    /// </summary>
    public class ExtendedGUID10BitUintValue : ExtendedGUID
    {
        [BitAttribute(6)]
        public ushort Type;
        [BitAttribute(10)]
        public ushort Value;
        public Guid GUID;

        /// <summary>
        /// Parse the ExtendedGUID10BitUintValue structure.
        /// </summary>
        /// <param name="s">A stream containing ExtendedGUID10BitUintValue structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            int temp = ReadUshort();
            int index = 0;
            this.Type = (ushort)GetBits(temp, index, 6);
            index += 6;
            this.Value = (ushort)GetBits(temp, index, 10);
            this.GUID = ReadGuid();
        }
    }
}
