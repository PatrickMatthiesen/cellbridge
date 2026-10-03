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
    /// 2.2.3.1	Sub-Responses
    /// </summary>
    public class FsshttpbSubResponse : BaseStructure
    {
        public bit32StreamObjectHeaderStart SubResponseStart;
        public CompactUnsigned64bitInteger RequestID;
        public CompactUnsigned64bitInteger RequestType;
        [BitAttribute(1)]
        public byte Status;
        [BitAttribute(7)]
        public byte Reserved;
        public ResponseError ResponseError;
        public object SubResponseData;
        public bit16StreamObjectHeaderEnd SubResponseEnd;

        /// <summary>
        /// Parse the FsshttpbSubResponse structure.
        /// </summary>
        /// <param name="s">A stream containing FsshttpbSubResponse structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.SubResponseStart = new bit32StreamObjectHeaderStart();
            this.SubResponseStart.Parse(s);
            this.RequestID = new CompactUnsigned64bitInteger();
            this.RequestID = RequestID.TryParse(s);
            this.RequestType = new CompactUnsigned64bitInteger();
            this.RequestType = RequestType.TryParse(s);
            byte tempByte = ReadByte();
            this.Status = GetBits(tempByte, 0, 1);
            this.Reserved = GetBits(tempByte, 1, 7);
            if (this.Status == 0x1)
            {
                this.ResponseError = new ResponseError();
                this.ResponseError.Parse(s);
            }
            else
            {
                if (this.RequestType.GetUint(RequestType) == 0x01)
                {
                    this.SubResponseData = new QueryAccessResponse();
                    ((QueryAccessResponse)this.SubResponseData).Parse(s);
                }
                else if (this.RequestType.GetUint(RequestType) == 0x02)
                {
                    this.SubResponseData = new QueryChangesResponse();
                    ((QueryChangesResponse)this.SubResponseData).Parse(s);
                }
                else if (this.RequestType.GetUint(RequestType) == 0x05)
                {
                    this.SubResponseData = new PutChangesResponse();
                    ((PutChangesResponse)this.SubResponseData).Parse(s);
                }
                else if (this.RequestType.GetUint(RequestType) == 0x0B)
                {
                    this.SubResponseData = new AllocateExtendedGUIDRange();
                    ((AllocateExtendedGUIDRange)this.SubResponseData).Parse(s);
                }

            }
            this.SubResponseEnd = new bit16StreamObjectHeaderEnd();
            this.SubResponseEnd.Parse(s);
            if (this.SubResponseEnd.Type != StreamObjectTypeHeaderEnd.SubResponse)
                throw new InvalidDataException("Expected SubResponse end header.");
        }
    }
}
