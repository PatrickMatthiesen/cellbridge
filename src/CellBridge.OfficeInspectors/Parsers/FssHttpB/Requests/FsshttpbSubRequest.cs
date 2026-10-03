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
    /// 2.2.2.1	Sub-Requests
    /// </summary>
    public class FsshttpbSubRequest : BaseStructure
    {
        public bit32StreamObjectHeaderStart SubRequestStart;
        public CompactUnsigned64bitInteger RequestID;
        public CompactUnsigned64bitInteger RequestType;
        public CompactUnsigned64bitInteger Priority;
        public TargetPartitionId TargetPartitionId;
        public object SubRequestData;
        public bit16StreamObjectHeaderEnd SubRequestEnd;

        /// <summary>
        /// Parse the FsshttpbSubRequests structure.
        /// </summary>
        /// <param name="s">A stream containing FsshttpbSubRequests structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.SubRequestStart = new bit32StreamObjectHeaderStart();
            this.SubRequestStart.Parse(s);
            this.RequestID = new CompactUnsigned64bitInteger();
            this.RequestID = this.RequestID.TryParse(s);
            this.RequestType = new CompactUnsigned64bitInteger();
            this.RequestType = this.RequestType.TryParse(s);
            this.Priority = new CompactUnsigned64bitInteger();
            this.Priority = this.Priority.TryParse(s);
            if (ContainsStreamObjectStart32BitHeader(0x83))
            {
                this.TargetPartitionId = new TargetPartitionId();
                this.TargetPartitionId.Parse(s);
            }

            switch (RequestType.GetUint(RequestType))
            {
                case 0x01:
                    this.SubRequestData = new QueryAccessRequest();
                    break;
                case 0x02:
                    this.SubRequestData = new QueryChangesRequest();
                    ((QueryChangesRequest)this.SubRequestData).Parse(s);
                    break;
                case 0x05:
                    this.SubRequestData = new PutChangesRequest();
                    ((PutChangesRequest)this.SubRequestData).Parse(s);
                    break;
                case 0x0B:
                    this.SubRequestData = new AllocateExtendedGUIDRangeRequest();
                    ((AllocateExtendedGUIDRangeRequest)this.SubRequestData).Parse(s);
                    break;
                default:
                    throw new Exception("The RequestType is not right.");
            }
            this.SubRequestEnd = new bit16StreamObjectHeaderEnd();
            this.SubRequestEnd.Parse(s);
            if (this.SubRequestEnd.Type != StreamObjectTypeHeaderEnd.SubRequest)
                throw new InvalidDataException("Expected SubRequest end header.");
        }
    }
}
