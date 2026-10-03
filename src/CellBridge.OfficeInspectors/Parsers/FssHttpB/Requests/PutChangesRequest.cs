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
    /// 2.2.2.1.4	Put Changes
    /// </summary>
    public class PutChangesRequest : BaseStructure
    {
        public bit32StreamObjectHeaderStart putChangesRequest;
        public ExtendedGUID StorageIndexExtendedGUID;
        public ExtendedGUID ExpectedStorageIndexExtendedGUID;
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
        public BinaryItem ContenVersionCoherencyCheck;
        public StringItemArray AuthorLogins;
        public byte Reserved1;
        public AdditionalFlags AdditionalFlags;
        public LockId LockId;
        public Knowledge ClientKnowledge;
        public DiagnosticRequestOptionInput DiagnosticRequestOptionInput;

        /// <summary>
        /// Parse the PutChangesRequest structure.
        /// </summary>
        /// <param name="s">A stream containing PutChangesRequest structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.putChangesRequest = new bit32StreamObjectHeaderStart();
            this.putChangesRequest.Parse(s);
            this.StorageIndexExtendedGUID = new ExtendedGUID();
            this.StorageIndexExtendedGUID = this.StorageIndexExtendedGUID.TryParse(s);
            this.ExpectedStorageIndexExtendedGUID = new ExtendedGUID();
            this.ExpectedStorageIndexExtendedGUID = this.ExpectedStorageIndexExtendedGUID.TryParse(s);
            byte tempByte = ReadByte();
            this.A = (byte)GetBits(tempByte, 0, 1);
            this.B = (byte)GetBits(tempByte, 1, 1);
            this.C = (byte)GetBits(tempByte, 2, 1);
            this.D = (byte)GetBits(tempByte, 3, 1);
            this.E = (byte)GetBits(tempByte, 4, 1);
            this.F = (byte)GetBits(tempByte, 5, 1);
            this.G = (byte)GetBits(tempByte, 6, 1);
            this.H = (byte)GetBits(tempByte, 7, 1);

            this.ContenVersionCoherencyCheck = new BinaryItem();
            this.ContenVersionCoherencyCheck.Parse(s);

            this.AuthorLogins = new StringItemArray();
            this.AuthorLogins.Parse(s);

            this.Reserved1 = ReadByte();

            if (ContainsStreamObjectStart32BitHeader(0x86))
            {
                this.AdditionalFlags = new AdditionalFlags();
                this.AdditionalFlags.Parse(s);
            }
            if (ContainsStreamObjectStart32BitHeader(0x85))
            {
                this.LockId = new LockId();
                this.LockId.Parse(s);
            }
            if (ContainsStreamObjectStart16BitHeader(0x10))
            {
                this.ClientKnowledge = new Knowledge();
                this.ClientKnowledge.Parse(s);
            }
            if (ContainsStreamObjectStart32BitHeader(0x8A))
            {
                this.DiagnosticRequestOptionInput = new DiagnosticRequestOptionInput();
                this.DiagnosticRequestOptionInput.Parse(s);
            }
        }
    }
}
