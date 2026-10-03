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
    /// 2.2.3.1.2	Query Changes
    /// </summary>
    public class QueryChangesResponse : BaseStructure
    {
        public bit32StreamObjectHeaderStart queryChangesResponse;
        public ExtendedGUID StorageIndexExtendedGUID;
        [BitAttribute(1)]
        public byte P;
        [BitAttribute(7)]
        public byte Reserved;
        public Knowledge Knowledge;
        public bit32StreamObjectHeaderStart FileHash;
        public CompactUnsigned64bitInteger Type;
        public BinaryItem DataHash;

        /// <summary>
        /// Parse the QueryChangesResponse structure.
        /// </summary>
        /// <param name="s">A stream containing QueryChangesResponse structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.queryChangesResponse = new bit32StreamObjectHeaderStart();
            this.queryChangesResponse.Parse(s);
            this.StorageIndexExtendedGUID = new ExtendedGUID();
            this.StorageIndexExtendedGUID = this.StorageIndexExtendedGUID.TryParse(s);
            byte tempbyte = ReadByte();
            this.P = GetBits(tempbyte, 0, 1);
            this.Reserved = GetBits(tempbyte, 1, 7);
            this.Knowledge = new Knowledge();
            this.Knowledge.Parse(s);
            if (ContainsStreamObjectStart32BitHeader(0x8E))
            {
                this.FileHash = new bit32StreamObjectHeaderStart();
                this.FileHash.Parse(s);
                this.Type = new CompactUnsigned64bitInteger();
                this.Type.TryParse(s);
                this.DataHash = new BinaryItem();
                this.DataHash.Parse(s);
            }
        }
    }
}
