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
    /// 2.2.1.1	Compact Unsigned 64-bit Integer
    /// </summary>
    public class CompactUnsigned64bitInteger : BaseStructure
    {
        /// <summary>
        /// Parse the CompactUnsigned64bitInteger structure.
        /// </summary>
        /// <param name="s">A stream containing CompactUnsigned64bitInteger structure.</param>
        public CompactUnsigned64bitInteger TryParse(Stream s)
        {
            base.Parse(s);
            byte temp = ReadByte();
            s.Position -= 1;
            CompactUnsigned64bitInteger compactUint64 = new CompactUnsigned64bitInteger();
            if (temp == 0x0)
            {
                compactUint64 = new CompactUintZero();
                compactUint64.Parse(s);
            }
            else if ((temp & 0x01) == 0x01)
            {
                compactUint64 = new CompactUint7bitvalues();
                compactUint64.Parse(s);
            }
            else if ((temp & 0x03) == 0x02)
            {
                compactUint64 = new CompactUint14bitvalues();
                compactUint64.Parse(s);
            }
            else if ((temp & 0x07) == 0x04)
            {
                compactUint64 = new CompactUint21bitvalues();
                compactUint64.Parse(s);
            }
            else if ((temp & 0x0F) == 0x08)
            {
                compactUint64 = new CompactUint28bitvalues();
                compactUint64.Parse(s);
            }
            else if ((temp & 0x1F) == 0x10)
            {
                compactUint64 = new CompactUint35bitvalues();
                compactUint64.Parse(s);
            }
            else if ((temp & 0x3F) == 0x20)
            {
                compactUint64 = new CompactUint42bitvalues();
                compactUint64.Parse(s);
            }
            else if ((temp & 0x7F) == 0x40)
            {
                compactUint64 = new CompactUint49bitvalues();
                compactUint64.Parse(s);
            }
            else if (temp == 0x80)
            {
                compactUint64 = new CompactUint64bitvalues();
                compactUint64.Parse(s);
            }
            return compactUint64;
        }

        /// <summary>
        /// get Uint feild in CompactUnsigned64bitInteger structure.
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
