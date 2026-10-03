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
    /// 2.2.1.12.7	Data Element Fragment Data Elements
    /// </summary>
    public class DataElementFragmentDataElement : BaseStructure
    {
        public StreamObjectHeader DataElementStart;
        public ExtendedGUID DataElementExtendedGUID;
        public SerialNumber SerialNumber;
        public CompactUnsigned64bitInteger DataElementType;
        public bit32StreamObjectHeaderStart DataElementFragment;
        public ExtendedGUID FragmentExtendedGUID;
        public CompactUnsigned64bitInteger FragmentDataElementSize;
        public FileChunkReference FragmentFileChunkReference;
        public BinaryItem FragmentData;
        public bit8StreamObjectHeaderEnd DataElementEnd;

        /// <summary>
        /// Parse the DataElementFragmentDataElement structure.
        /// </summary>
        /// <param name="s">A stream containing DataElementFragmentDataElement structure.</param>
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
            this.DataElementFragment = new bit32StreamObjectHeaderStart();
            this.DataElementFragment.Parse(s);
            this.FragmentExtendedGUID = new ExtendedGUID();
            this.FragmentExtendedGUID = this.FragmentExtendedGUID.TryParse(s);
            this.FragmentDataElementSize = new CompactUnsigned64bitInteger();
            this.FragmentDataElementSize = this.FragmentDataElementSize.TryParse(s);
            this.FragmentFileChunkReference = new FileChunkReference();
            this.FragmentFileChunkReference.Parse(s);
            this.FragmentData = new BinaryItem();
            this.FragmentData.Parse(s);
            this.DataElementEnd = new bit8StreamObjectHeaderEnd();
            this.DataElementEnd.Parse(s);
            if (this.DataElementEnd.Type != StreamObjectTypeHeaderEnd.DataElement)
                throw new InvalidDataException("Expected DataElement end header.");
        }
    }
}
