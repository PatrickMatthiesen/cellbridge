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
    /// 2.2.2.1.3	Query Changes
    /// </summary>
    public class QueryChangesRequest : BaseStructure
    {
        public bit32StreamObjectHeaderStart queryChangesRequest;
        [BitAttribute(1)]
        public byte A;
        [BitAttribute(1)]
        public byte B;
        [BitAttribute(1)]
        public byte C;
        [BitAttribute(1)]
        public byte D;
        [BitAttribute(1)]
        public byte E;
        [BitAttribute(1)]
        public byte F;
        [BitAttribute(1)]
        public byte G;
        [BitAttribute(1)]
        public byte H;
        [BitAttribute(1)]
        public byte? UserContentEquivalentVersionOk;
        [BitAttribute(7)]
        public byte? ReservedMustBeZero;
        public bit32StreamObjectHeaderStart queryChangesRequestArguments;
        [BitAttribute(1)]
        public byte? F2;
        [BitAttribute(1)]
        public byte? G2;
        [BitAttribute(6)]
        public byte? H2;
        public CellID CellID;
        public bit32StreamObjectHeaderStart QueryChangesDataConstraints;
        public CompactUnsigned64bitInteger MaximumDataElements;
        public bit16StreamObjectHeaderStart QueryChangesVersioning;
        public uint? MajorVersionNumber;
        public uint? MinorVersionNumber;
        public byte[] VersionToken;
        public Filter[] QueryChangesFilters;
        public Knowledge Knowledge;

        /// <summary>
        /// Parse the Filter structure.
        /// </summary>
        /// <param name="s">A stream containing Filter structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.queryChangesRequest = new bit32StreamObjectHeaderStart();
            this.queryChangesRequest.Parse(s);
            byte tempByte = ReadByte();
            this.A = GetBits(tempByte, 0, 1);
            this.B = GetBits(tempByte, 1, 1);
            this.C = GetBits(tempByte, 2, 1);
            this.D = GetBits(tempByte, 3, 1);
            this.E = GetBits(tempByte, 4, 1);
            this.F = GetBits(tempByte, 5, 1);
            this.G = GetBits(tempByte, 6, 1);
            this.H = GetBits(tempByte, 7, 1);
            if (this.queryChangesRequest.Length == 2)
            {
                byte tempb = ReadByte();
                this.UserContentEquivalentVersionOk = GetBits(tempb, 0, 1);
                this.ReservedMustBeZero = GetBits(tempb, 1, 7);
            }

            if (ContainsStreamObjectStart32BitHeader(0x05B))
            {
                this.queryChangesRequestArguments = new bit32StreamObjectHeaderStart();
                this.queryChangesRequestArguments.Parse(s);

                byte temp2 = ReadByte();
                this.F2 = GetBits(temp2, 0, 1);
                this.G2 = GetBits(temp2, 1, 1);
                this.H2 = GetBits(temp2, 2, 6);
            }

            this.CellID = new CellID();
            this.CellID.Parse(s);
            if (ContainsStreamObjectStart32BitHeader(0x059))
            {
                this.QueryChangesDataConstraints = new bit32StreamObjectHeaderStart();
                this.QueryChangesDataConstraints.Parse(s);
            }
            if (this.QueryChangesDataConstraints != null)
            {
                this.MaximumDataElements = new CompactUnsigned64bitInteger();
                this.MaximumDataElements = this.MaximumDataElements.TryParse(s);
            }

            if (ContainsStreamObjectStart16BitHeader(0x30))
            {
                this.QueryChangesVersioning = new bit16StreamObjectHeaderStart();
                this.QueryChangesVersioning.Parse(s);
                if (this.QueryChangesVersioning.Length == 8)
                {
                    this.MajorVersionNumber = ReadUint();
                    this.MinorVersionNumber = ReadUint();
                }
                else
                {
                    this.VersionToken = ReadBytes(this.QueryChangesVersioning.Length);
                }
            }

            List<Filter> FilterList = new List<Filter>();
            while (ContainsStreamObjectStart32BitHeader(0x47))
            {
                Filter tempFilter = new Filter();
                tempFilter.Parse(s);
                FilterList.Add(tempFilter);
            }
            this.QueryChangesFilters = FilterList.ToArray();

            if (ContainsStreamObjectStart16BitHeader(0x10))
            {
                this.Knowledge = new Knowledge();
                this.Knowledge.Parse(s);
            }
        }
    }
}
