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
    /// 2.2.1.5	Stream Object Header
    /// </summary>
    public class StreamObjectHeader : BaseStructure
    {
        /// <summary>
        /// Parse the StreamObjectHeader structure.
        /// </summary>
        /// <param name="s">A stream containing StreamObjectHeader structure.</param>
        public StreamObjectHeader TryParse(Stream s)
        {
            base.Parse(s);
            byte temp = ReadByte();
            s.Position -= 1;
            StreamObjectHeader streamObjectHeader = new StreamObjectHeader();
            if ((temp & 0x03) == 0x0)
            {
                streamObjectHeader = new bit16StreamObjectHeaderStart();
                streamObjectHeader.Parse(s);
            }
            else if ((temp & 0x03) == 0x02)
            {
                streamObjectHeader = new bit32StreamObjectHeaderStart();
                streamObjectHeader.Parse(s);
            }
            return streamObjectHeader;
        }

        /// <summary>
        /// get Uint feild in StreamObjectHeader structure.
        /// </summary>
        /// <returns>The value of Uint feild</returns>
        public ulong GetUint(CompactUnsigned64bitInteger objectVal)
        {
            if (objectVal is CompactUintZero)
            {
                return (objectVal as CompactUintZero).Uint;
            }
            else if (objectVal is CompactUint7bitvalues)
            {
                return (objectVal as CompactUint7bitvalues).Uint;
            }
            else if (objectVal is CompactUint14bitvalues)
            {
                return (objectVal as CompactUint14bitvalues).Uint;
            }
            else if (objectVal is CompactUint21bitvalues)
            {
                return (objectVal as CompactUint21bitvalues).Uint;
            }
            else if (objectVal is CompactUint28bitvalues)
            {
                return (objectVal as CompactUint28bitvalues).Uint;
            }
            else if (objectVal is CompactUint35bitvalues)
            {
                return (objectVal as CompactUint35bitvalues).Uint;
            }
            else if (objectVal is CompactUint42bitvalues)
            {
                return (objectVal as CompactUint42bitvalues).Uint;
            }
            else if (objectVal is CompactUint49bitvalues)
            {
                return (objectVal as CompactUint49bitvalues).Uint;
            }
            else if (objectVal is CompactUint64bitvalues)
            {
                return (objectVal as CompactUint64bitvalues).Uint;
            }
            else
            {
                throw new Exception("The CompactUnsigned64bitInteger type is not right.");
            }
        }
    }
}
