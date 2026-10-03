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
    /// 2.2.2	Request Message Syntax
    /// </summary>
    public class FsshttpbRequest : BaseStructure
    {
        public ushort ProtocolVersion;
        public ushort MinimumVersion;
        public ulong Signature;
        public bit32StreamObjectHeaderStart RequestStart;
        public bit32StreamObjectHeaderStart UserAgentStart;
        public bit32StreamObjectHeaderStart UserAgentGUID;
        public Guid? GUID;
        public bit32StreamObjectHeaderStart UserAgentClientAndPlatform;
        public CompactUnsigned64bitInteger ClientCount;
        public byte[] ClientByteArray;
        public CompactUnsigned64bitInteger PlatformCount;
        public byte[] PlatformByteArray;
        public bit32StreamObjectHeaderStart UserAgentVersion;
        public uint Version;
        public bit16StreamObjectHeaderEnd UserAgentEnd;
        public bit32StreamObjectHeaderStart RequestHashingOptionsDeclaration;
        public CompactUnsigned64bitInteger RequestHasingSchema;
        [BitAttribute(1)]
        public byte? A;
        [BitAttribute(1)]
        public byte? B;
        [BitAttribute(1)]
        public byte? C;
        [BitAttribute(1)]
        public byte? D;
        [BitAttribute(4)]
        public byte? E;
        public bit32StreamObjectHeaderStart CellRoundtrioOptions;
        [BitAttribute(1)]
        public byte? F;
        [BitAttribute(1)]
        public byte? G;
        [BitAttribute(6)]
        public byte? H;
        public FsshttpbSubRequest[] SubRequest;
        public DataElementPackage DataElementPackage;
        public bit16StreamObjectHeaderEnd RequestEnd;

        /// <summary>
        /// Parse the FsshttpbRequest structure.
        /// </summary>
        /// <param name="s">A stream containing FsshttpbRequest structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.ProtocolVersion = ReadUshort();
            this.MinimumVersion = ReadUshort();
            this.Signature = ReadUlong();
            this.RequestStart = new bit32StreamObjectHeaderStart();
            this.RequestStart.Parse(s);

            if (ContainsStreamObjectStart32BitHeader(0x05D))
            {
                this.UserAgentStart = new bit32StreamObjectHeaderStart();
                this.UserAgentStart.Parse(s);

                if (ContainsStreamObjectStart32BitHeader(0x055))
                {
                    this.UserAgentGUID = new bit32StreamObjectHeaderStart();
                    this.UserAgentGUID.Parse(s);
                }
                if (this.UserAgentGUID != null)
                {
                    this.GUID = ReadGuid();
                }
                if (ContainsStreamObjectStart32BitHeader(0x8B))
                {
                    this.UserAgentClientAndPlatform = new bit32StreamObjectHeaderStart();
                    this.UserAgentClientAndPlatform.Parse(s);
                }

                if (this.UserAgentClientAndPlatform != null)
                {
                    this.ClientCount = new CompactUnsigned64bitInteger();
                    this.ClientCount = this.ClientCount.TryParse(s);
                    this.ClientByteArray = ReadBytes((int)this.ClientCount.GetUint(this.ClientCount));
                    this.PlatformCount = new CompactUnsigned64bitInteger();
                    this.PlatformCount = this.PlatformCount.TryParse(s);
                    this.PlatformByteArray = ReadBytes((int)this.PlatformCount.GetUint(this.PlatformCount));
                }

                this.UserAgentVersion = new bit32StreamObjectHeaderStart();
                this.UserAgentVersion.Parse(s);
                this.Version = ReadUint();
                this.UserAgentEnd = new bit16StreamObjectHeaderEnd();
                this.UserAgentEnd.Parse(s);
            if (this.UserAgentEnd.Type != StreamObjectTypeHeaderEnd.UserAgent)
                throw new InvalidDataException("Expected UserAgent end header.");
            }
            if (ContainsStreamObjectStart32BitHeader(0x88))
            {
                this.RequestHashingOptionsDeclaration = new bit32StreamObjectHeaderStart();
                this.RequestHashingOptionsDeclaration.Parse(s);
                this.RequestHasingSchema = new CompactUnsigned64bitInteger();
                this.RequestHasingSchema = this.RequestHasingSchema.TryParse(s);
                byte tempByte = ReadByte();
                this.A = GetBits(tempByte, 0, 1);
                this.B = GetBits(tempByte, 1, 1);
                this.C = GetBits(tempByte, 2, 1);
                this.D = GetBits(tempByte, 3, 1);
                this.E = GetBits(tempByte, 4, 4);
            }
            if (ContainsStreamObjectStart32BitHeader(0x8D))
            {
                this.CellRoundtrioOptions = new bit32StreamObjectHeaderStart();
                this.CellRoundtrioOptions.Parse(s);
                byte tempByte = ReadByte();
                this.F = GetBits(tempByte, 0, 1);
                this.G = GetBits(tempByte, 1, 1);
                this.H = GetBits(tempByte, 2, 6);
            }

            if (ContainsStreamObjectStart32BitHeader(0x042))
            {
                List<FsshttpbSubRequest> tempRequest = new List<FsshttpbSubRequest>();
                do
                {
                    FsshttpbSubRequest subRequest = new FsshttpbSubRequest();
                    subRequest.Parse(s);
                    tempRequest.Add(subRequest);
                    this.SubRequest = tempRequest.ToArray();
                } while (ContainsStreamObjectStart32BitHeader(0x042));
            }
            if (ContainsStreamObjectHeader(0x15))
            {
                this.DataElementPackage = new DataElementPackage();
                this.DataElementPackage.Parse(s);
            }
            this.RequestEnd = new bit16StreamObjectHeaderEnd();
            this.RequestEnd.Parse(s);
            if (this.RequestEnd.Type != StreamObjectTypeHeaderEnd.Request)
                throw new InvalidDataException("Expected Request end header.");
        }
    }
}
