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
    /// 2.2.2.1.3.1	Filters
    /// </summary>
    public class Filter : BaseStructure
    {
        public bit32StreamObjectHeaderStart QueryChangesFilterStart;
        public FilterType FilterType;
        public byte FilterOperation;
        public object QueryChangesFilterData;
        public bit16StreamObjectHeaderEnd QueryChangesFilterEnd;
        public bit32StreamObjectHeaderStart QueryChangesFilterFlags;
        [BitAttribute(1)]
        public byte? F;
        [BitAttribute(7)]
        public byte? Reserved;

        /// <summary>
        /// Parse the Filter structure.
        /// </summary>
        /// <param name="s">A stream containing Filter structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.QueryChangesFilterStart = new bit32StreamObjectHeaderStart();
            this.QueryChangesFilterStart.Parse(s);
            this.FilterType = (FilterType)ReadByte();
            this.FilterOperation = ReadByte();

            switch (this.FilterType)
            {
                case Parsers.FilterType.AllFilter:
                case Parsers.FilterType.StorageIndexReferencedDataElementsFilter:
                    break;
                case Parsers.FilterType.DataElementIDsFilter:
                    this.QueryChangesFilterData = new DataElementIDsFilter();
                    ((DataElementIDsFilter)this.QueryChangesFilterData).Parse(s);
                    break;
                case Parsers.FilterType.DataElementTypeFilter:
                    this.QueryChangesFilterData = new DataElementTypeFilter();
                    ((DataElementTypeFilter)this.QueryChangesFilterData).Parse(s);
                    break;
                case Parsers.FilterType.CellIDFilter:
                    this.QueryChangesFilterData = new CellIDFilter();
                    ((CellIDFilter)this.QueryChangesFilterData).Parse(s);
                    break;
                case Parsers.FilterType.CustomFilter:
                    this.QueryChangesFilterData = new CustomFilter();
                    ((CustomFilter)this.QueryChangesFilterData).Parse(s);
                    break;
                case Parsers.FilterType.HierarchyFilter:
                    this.QueryChangesFilterData = new HierarchyFilter();
                    ((HierarchyFilter)this.QueryChangesFilterData).Parse(s);
                    break;
                default:
                    throw new Exception("The FilterType is not right.");
            }

            this.QueryChangesFilterEnd = new bit16StreamObjectHeaderEnd();
            this.QueryChangesFilterEnd.Parse(s);
            if (this.QueryChangesFilterEnd.Type != StreamObjectTypeHeaderEnd.QueryChangesFilter)
                throw new InvalidDataException("Expected QueryChangesFilter end header.");

            if (ContainsStreamObjectStart32BitHeader(0x87))
            {
                this.QueryChangesFilterFlags = new bit32StreamObjectHeaderStart();
                this.QueryChangesFilterFlags.Parse(s);
            }
            if (this.QueryChangesFilterFlags != null)
            {
                byte tempByte = ReadByte();
                this.F = GetBits(tempByte, 0, 1);
                this.Reserved = GetBits(tempByte, 1, 7);
            }
        }
    }
}
