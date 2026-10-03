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
    /// 2.2.1.14	String Item Array
    /// </summary>
    public class StringItemArray : BaseStructure
    {
        public CompactUnsigned64bitInteger Count;
        public StringItem[] Content;

        /// <summary>
        /// Parse the StringItemArray structure.
        /// </summary>
        /// <param name="s">A stream containing StringItemArray structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.Count = new CompactUnsigned64bitInteger();
            this.Count = this.Count.TryParse(s);
            List<StringItem> tempContent = new List<StringItem>();
            if (this.Count.GetUint(Count) > 0)
            {
                ulong tempCount = this.Count.GetUint(Count);
                do
                {
                    StringItem tempGuid = new StringItem();
                    tempGuid.Parse(s);
                    tempContent.Add(tempGuid);
                    tempCount--;
                } while (tempCount > 0);
                this.Content = tempContent.ToArray();
            }
        }
    }
}
