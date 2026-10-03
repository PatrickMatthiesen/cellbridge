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
    /// 2.2.1.12.2	Storage Index Data Element
    /// </summary>
    public class StorageIndexDataElement : BaseStructure
    {
        public StreamObjectHeader DataElementStart;
        public ExtendedGUID DataElementExtendedGUID;
        public SerialNumber SerialNumber;
        public CompactUnsigned64bitInteger DataElementType;
        public object[] StorageIndexDataElementData;
        public bit8StreamObjectHeaderEnd DataElementEnd;

        /// <summary>
        /// Parse the StorageIndexDataElement structure.
        /// </summary>
        /// <param name="s">A stream containing StorageIndexDataElement structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.DataElementStart = new StreamObjectHeader().TryParse(s);
            this.DataElementExtendedGUID = new ExtendedGUID();
            this.DataElementExtendedGUID = this.DataElementExtendedGUID.TryParse(s);
            this.SerialNumber = new SerialNumber();
            this.SerialNumber = this.SerialNumber.TryParse(s);
            this.DataElementType = new CompactUnsigned64bitInteger();
            this.DataElementType = this.DataElementType.TryParse(s);

            int StorageIndexType = (CurrentByte() >> 3) & 0x3F;
            List<object> DataList = new List<object>();
            while ((CurrentByte() & 0x03) == 0x0 && (StorageIndexType == 0x11 || StorageIndexType == 0x0E || StorageIndexType == 0x0D))
            {
                switch (StorageIndexType)
                {
                    case 0x11:
                        StorageIndexManifestMappingValues ManifestMappingValue = new StorageIndexManifestMappingValues();
                        ManifestMappingValue.Parse(s);
                        DataList.Add(ManifestMappingValue);
                        break;
                    case 0x0E:
                        StorageIndexCellMappingValues CellMappingValue = new StorageIndexCellMappingValues();
                        CellMappingValue.Parse(s);
                        DataList.Add(CellMappingValue);
                        break;
                    case 0x0D:
                        StorageIndexRevisionMappingValues RevisionMappingValue = new StorageIndexRevisionMappingValues();
                        RevisionMappingValue.Parse(s);
                        DataList.Add(RevisionMappingValue);
                        break;
                    default:
                        throw new Exception("The StorageIndexType is not right.");
                }
                StorageIndexType = (CurrentByte() >> 3) & 0x3F;
            }
            this.StorageIndexDataElementData = DataList.ToArray();
            this.DataElementEnd = new bit8StreamObjectHeaderEnd();
            this.DataElementEnd.Parse(s);
            if (this.DataElementEnd.Type != StreamObjectTypeHeaderEnd.DataElement)
                throw new InvalidDataException("Expected DataElement end header.");
        }
    }
}
