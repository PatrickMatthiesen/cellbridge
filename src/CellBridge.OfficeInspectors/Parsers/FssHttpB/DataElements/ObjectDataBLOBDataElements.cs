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
    /// 2.2.1.12.8	Object Data BLOB Data Elements
    /// </summary>
    public class ObjectDataBLOBDataElements : BaseStructure
    {
        public StreamObjectHeader DataElementStart;
        public ExtendedGUID DataElementExtendedGUID;
        public SerialNumber SerialNumber;
        public CompactUnsigned64bitInteger DataElementType;
        public StreamObjectHeader ObjectDataBLOB;
        // Compatibility container only: Length comes from ObjectDataBLOB, not the wire payload.
        public BinaryItem Data;
        public bit8StreamObjectHeaderEnd DataElementEnd;

        /// <summary>
        /// Parse the ObjectDataBLOBDataElements structure.
        /// </summary>
        /// <param name="s">A stream containing ObjectDataBLOBDataElements structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.DataElementStart = new StreamObjectHeader().TryParse(s);
            int metadataLength = HeaderLength(this.DataElementStart, StreamObjectTypeHeaderStart.DataElement, 1);
            long metadataStart = s.Position;
            this.DataElementExtendedGUID = new ExtendedGUID();
            this.DataElementExtendedGUID = this.DataElementExtendedGUID.TryParse(s);
            this.SerialNumber = new SerialNumber();
            this.SerialNumber = this.SerialNumber.TryParse(s);
            this.DataElementType = new CompactUnsigned64bitInteger();
            this.DataElementType = this.DataElementType.TryParse(s);
            if (s.Position - metadataStart != metadataLength || this.DataElementType.GetUint(this.DataElementType) != 0x0A)
                throw new InvalidDataException("Invalid Object Data BLOB element metadata.");
            this.ObjectDataBLOB = new StreamObjectHeader();
            this.ObjectDataBLOB = this.ObjectDataBLOB.TryParse(s);
            int length = HeaderLength(this.ObjectDataBLOB, StreamObjectTypeHeaderStart.ObjectDataBLOB, 0);
            if (length > s.Length - s.Position - 1)
                throw new InvalidDataException("Object Data BLOB length exceeds the remaining payload and end header.");
            // MS-FSSHTTPB 2.2.1.12.8 defines raw opaque bytes here, not a Binary Item.
            // Keep the imported public Data.Content/Length API without consuming a length prefix.
            this.Data = new BinaryItem
            {
                Length = new CompactUint64bitvalues { A = 0x80, Uint = (ulong)length },
                Content = ReadBytes(length)
            };
            this.DataElementEnd = new bit8StreamObjectHeaderEnd();
            this.DataElementEnd.Parse(s);
            if (this.DataElementEnd.Type != StreamObjectTypeHeaderEnd.DataElement)
                throw new InvalidDataException("Expected DataElement end header.");
        }
        private static int HeaderLength(StreamObjectHeader header, StreamObjectTypeHeaderStart type, byte compound)
        {
            ulong length;
            if (header is bit16StreamObjectHeaderStart shortHeader && shortHeader.Type == type && shortHeader.B == compound)
                length = shortHeader.Length;
            else if (header is bit32StreamObjectHeaderStart longHeader && longHeader.Type == type && longHeader.B == compound)
                length = longHeader.Length == 32767
                    ? longHeader.LargeLength.GetUint(longHeader.LargeLength)
                    : (ulong)longHeader.Length;
            else
                throw new InvalidDataException("Unexpected Object Data BLOB stream-object header.");
            if (length > int.MaxValue)
                throw new InvalidDataException("Object Data BLOB declared length exceeds the inspector limit.");
            return (int)length;
        }
    }
}
