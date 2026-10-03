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
    /// 2.2.1.7	Extended GUID
    /// </summary>
    public class ExtendedGUID : BaseStructure
    {
        /// <summary>
        /// Parse the ExtendedGUID structure.
        /// </summary>
        /// <param name="s">A stream containing ExtendedGUID structure.</param>
        /// <returns>Return parserd ExtendedGUID structure.</returns>
        public ExtendedGUID TryParse(Stream s)
        {
            base.Parse(s);
            byte temp = ReadByte();
            s.Position -= 1;
            ExtendedGUID extendedGUID = new ExtendedGUID();
            if (temp == 0x0)
            {
                extendedGUID = new ExtendedGUIDNullValue();
                extendedGUID.Parse(s);
            }
            else if ((temp & 0x07) == 0x4)
            {
                extendedGUID = new ExtendedGUID5BitUintValue();
                extendedGUID.Parse(s);

            }
            else if ((temp & 0x3F) == 0x20)
            {
                extendedGUID = new ExtendedGUID10BitUintValue();
                extendedGUID.Parse(s);
            }
            else if ((temp & 0x7F) == 0x40)
            {
                extendedGUID = new ExtendedGUID17BitUintValue();
                extendedGUID.Parse(s);
            }
            else if (temp == 0x80)
            {
                extendedGUID = new ExtendedGUID32BitUintValue();
                extendedGUID.Parse(s);
            }

            return extendedGUID;
        }

        /// <summary>
        /// Get GUID feild in ExtendedGUID structure.
        /// </summary>
        /// <returns>The value of GUID feild</returns>
        public Guid GetGUID(ExtendedGUID extendedGUID)
        {
            if (extendedGUID is ExtendedGUIDNullValue)
            {
                return Guid.Empty;
            }
            if (extendedGUID is ExtendedGUID5BitUintValue)
            {
                return (extendedGUID as ExtendedGUID5BitUintValue).GUID;
            }
            else if (extendedGUID is ExtendedGUID10BitUintValue)
            {
                return (extendedGUID as ExtendedGUID10BitUintValue).GUID;
            }
            else if (extendedGUID is ExtendedGUID17BitUintValue)
            {
                return (extendedGUID as ExtendedGUID17BitUintValue).GUID;
            }
            else if (extendedGUID is ExtendedGUID32BitUintValue)
            {
                return (extendedGUID as ExtendedGUID32BitUintValue).GUID;
            }
            else
            {
                throw new Exception("The CompactUnsigned64bitInteger type is not right.");
            }
        }

        /// <summary>
        /// Get Value feild in ExtendedGUID structure.
        /// </summary>
        /// <returns>The value of Value feild</returns>
        public uint GetValue(ExtendedGUID extendedGUID)
        {
            if (extendedGUID is ExtendedGUIDNullValue)
            {
                return 0;
            }
            if (extendedGUID is ExtendedGUID5BitUintValue)
            {
                return (extendedGUID as ExtendedGUID5BitUintValue).Value;
            }
            else if (extendedGUID is ExtendedGUID10BitUintValue)
            {
                return (extendedGUID as ExtendedGUID10BitUintValue).Value;
            }
            else if (extendedGUID is ExtendedGUID17BitUintValue)
            {
                return (extendedGUID as ExtendedGUID17BitUintValue).Value;
            }
            else if (extendedGUID is ExtendedGUID32BitUintValue)
            {
                return (extendedGUID as ExtendedGUID32BitUintValue).Value;
            }
            else
            {
                throw new Exception("The CompactUnsigned64bitInteger type is not right.");
            }
        }


        /// <summary>
        /// Get Type feild in ExtendedGUID structure.
        /// </summary>
        /// <returns>The value of Type feild</returns>
        public uint GetType(ExtendedGUID extendedGUID)
        {
            if (extendedGUID is ExtendedGUIDNullValue)
            {
                return 0;
            }
            if (extendedGUID is ExtendedGUID5BitUintValue)
            {
                return (extendedGUID as ExtendedGUID5BitUintValue).Type;
            }
            else if (extendedGUID is ExtendedGUID10BitUintValue)
            {
                return (extendedGUID as ExtendedGUID10BitUintValue).Type;
            }
            else if (extendedGUID is ExtendedGUID17BitUintValue)
            {
                return (extendedGUID as ExtendedGUID17BitUintValue).Type;
            }
            else if (extendedGUID is ExtendedGUID32BitUintValue)
            {
                return (extendedGUID as ExtendedGUID32BitUintValue).Type;
            }
            else
            {
                throw new Exception("The CompactUnsigned64bitInteger type is not right.");
            }
        }
    }
}
