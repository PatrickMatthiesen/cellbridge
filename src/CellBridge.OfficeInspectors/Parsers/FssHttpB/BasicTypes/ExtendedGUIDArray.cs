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
    /// 2.2.1.8	Extended GUID Array
    /// </summary>
    public class ExtendedGUIDArray : BaseStructure
    {
        public CompactUnsigned64bitInteger Count;
        public ExtendedGUID[] Content;

        /// <summary>
        /// Parse the ExtendedGUIDArray structure.
        /// </summary>
        /// <param name="s">A stream containing ExtendedGUIDArray structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.Count = new CompactUnsigned64bitInteger();
            this.Count = this.Count.TryParse(s);
            List<ExtendedGUID> tempContent = new List<ExtendedGUID>();
            if (this.Count.GetUint(Count) > 0)
            {
                ulong tempCount = this.Count.GetUint(Count);
                ExtendedGUID tempGuid = new ExtendedGUID();
                do
                {
                    tempGuid = tempGuid.TryParse(s);
                    tempContent.Add(tempGuid);
                    tempCount--;
                } while (tempCount > 0);
                this.Content = tempContent.ToArray();
            }
        }
    }
}
