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
    /// 2.2.3	Response Message Syntax
    /// </summary>
    public class FsshttpbResponse : BaseStructure
    {
        public ushort ProtocolVersion;
        public ushort MinimumVersion;
        public ulong Signature;
        public bit32StreamObjectHeaderStart ResponseStart;
        [BitAttribute(1)]
        public byte Status;
        [BitAttribute(7)]
        public byte Reserved;
        public ResponseError ResponseError;
        public DataElementPackage DataElementPackage;
        public FsshttpbSubResponse[] SubResponses;
        public bit16StreamObjectHeaderEnd ResponseEnd;

        /// <summary>
        /// Parse the FsshttpbResponse structure.
        /// </summary>
        /// <param name="s">A stream containing FsshttpbResponse structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.ProtocolVersion = ReadUshort();
            this.MinimumVersion = ReadUshort();
            this.Signature = ReadUlong();
            this.ResponseStart = new bit32StreamObjectHeaderStart();
            this.ResponseStart.Parse(s);
            byte tempByte = ReadByte();
            this.Status = GetBits(tempByte, 0, 1);
            this.Reserved = GetBits(tempByte, 1, 7);
            // A ExtendedGUID list contain encrypted Object ExtendedGUID.
            List<ExtendedGUID> encryptedObjectIDList = new List<ExtendedGUID>();
            bool is2ndParse = false;
            if (this.Status == 0x1)
            {
                this.ResponseError = new ResponseError();
                this.ResponseError.Parse(s);
            }
            else
            {
                if (ContainsStreamObjectHeader(0x15))
                {
                    this.DataElementPackage = new DataElementPackage();

                    // Parse DataElementPackage for OneStore message
                    if (Context.IsOneStore)
                    {
                        long startIndex = s.Position;
                        this.DataElementPackage.Parse(s, is2ndParse);
                        s.Position = startIndex;
                        is2ndParse = true;
                        this.DataElementPackage.Parse(s, is2ndParse);
                        is2ndParse = false;
                        Context.EncryptedObjectGroupIds.Clear();
                    }
                    else //Parse DataElementPackage for FSSHTTPB message
                    {
                        this.DataElementPackage.Parse(s);
                    }
                }

                if (ContainsStreamObjectStart32BitHeader(0x041))
                {
                    List<FsshttpbSubResponse> tempResponses = new List<FsshttpbSubResponse>();
                    do
                    {
                        FsshttpbSubResponse subResponse = new FsshttpbSubResponse();
                        subResponse.Parse(s);
                        tempResponses.Add(subResponse);
                        this.SubResponses = tempResponses.ToArray();
                    } while (ContainsStreamObjectStart32BitHeader(0x041));
                }
            }

            this.ResponseEnd = new bit16StreamObjectHeaderEnd();
            this.ResponseEnd.Parse(s);
            if (this.ResponseEnd.Type != StreamObjectTypeHeaderEnd.Response)
                throw new InvalidDataException("Expected Response end header.");

        }
    }
}
