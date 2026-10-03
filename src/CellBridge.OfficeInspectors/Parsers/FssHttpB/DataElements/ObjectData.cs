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
    /// 2.2.1.12.6.4	Object Data
    /// </summary>
    public class ObjectData : BaseStructure
    {
        public StreamObjectHeader ObjectGroupObjectDataOrExcludedData;
        public ExtendedGUIDArray ObjectExtendedGUIDArray;
        public CellIDArray CellIDArray;

        public CompactUnsigned64bitInteger DataSize;
        public object Data;
        public JCID JCID;
        public ObjectSpaceObjectPropSet ObjectSpaceObjectPropSet;

        /// <summary>
        /// Parse the ObjectData structure.
        /// </summary>
        /// <param name="s">A stream containing ObjectData structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.ObjectGroupObjectDataOrExcludedData = new StreamObjectHeader();
            this.ObjectGroupObjectDataOrExcludedData = this.ObjectGroupObjectDataOrExcludedData.TryParse(s);
            this.ObjectExtendedGUIDArray = new ExtendedGUIDArray();
            this.ObjectExtendedGUIDArray.Parse(s);
            this.CellIDArray = new CellIDArray();
            this.CellIDArray.Parse(s);
            this.DataSize = new CompactUnsigned64bitInteger();
            this.DataSize = this.DataSize.TryParse(s);

            if (ContainsStreamObjectStart16BitHeader(0x20))
            {
                this.Data = new IntermediateNodeObjectData();
                ((IntermediateNodeObjectData)this.Data).Parse(s);

            }
            else if (ContainsStreamObjectStart16BitHeader(0x1F))
            {
                this.Data = new LeafNodeObjectData();
                ((LeafNodeObjectData)this.Data).Parse(s);
            }
            else if ((int)this.DataSize.GetUint(this.DataSize) > 0)
            {

                byte[] dataarray = ReadBytes((int)this.DataSize.GetUint(this.DataSize));

                if (Utilities.IsEditorsTableHeader(dataarray))
                {
                    this.Data = dataarray;
                    Context.IsNextEditorTable = true;
                }
                else if (Utilities.IsZIPFileHeaderMatch(dataarray, Utilities.LocalFileHeader) || Utilities.IsZIPFileHeaderMatch(dataarray, Utilities.CentralDirectoryHeader))
                {
                    s.Position -= (int)this.DataSize.GetUint(this.DataSize);
                    long startPostion = s.Position;
                    this.Data = new ZIPFileStructure();
                    ((ZIPFileStructure)this.Data).Parse(s);
                    if (s.Position - startPostion > (long)this.DataSize.GetUint(this.DataSize))
                    {
                        this.Data = dataarray;
                    }
                    s.Position = startPostion + (long)this.DataSize.GetUint(this.DataSize);
                }
                else if (Utilities.IsPNGHeader(dataarray))
                {
                    this.Data = dataarray;
                }
                else if (Context.IsNextEditorTable)
                {
                    this.Data = Utilities.ReadEditorsTable(dataarray);
                    Context.IsNextEditorTable = false;
                }
                else
                {

                    this.Data = dataarray;
                }

            }
        }

        /// <summary>
        /// Parse the ObjectData structure for ONESTORE message.
        /// </summary>
        /// <param name="s">A stream containing ObjectData structure.</param>
        /// <param name="partitionId">A compact unsigned 64-bit integer that specifies the object partition of the object.</param>
        public override void Parse(Stream s, ulong partitionId)
        {
            base.Parse(s);
            this.ObjectGroupObjectDataOrExcludedData = new StreamObjectHeader();
            this.ObjectGroupObjectDataOrExcludedData = this.ObjectGroupObjectDataOrExcludedData.TryParse(s);
            this.ObjectExtendedGUIDArray = new ExtendedGUIDArray();
            this.ObjectExtendedGUIDArray.Parse(s);
            this.CellIDArray = new CellIDArray();
            this.CellIDArray.Parse(s);
            this.DataSize = new CompactUnsigned64bitInteger();
            this.DataSize = this.DataSize.TryParse(s);

            if (ContainsStreamObjectStart16BitHeader(0x20))
            {
                this.Data = new IntermediateNodeObjectData();
                ((IntermediateNodeObjectData)this.Data).Parse(s);

            }
            else if (ContainsStreamObjectStart16BitHeader(0x1F))
            {
                this.Data = new LeafNodeObjectData();
                ((LeafNodeObjectData)this.Data).Parse(s);
            }
            else if ((int)this.DataSize.GetUint(this.DataSize) > 0)
            {
                // Record start read Position of Stream.
                long startPosition = s.Position;
                byte[] dataarray = ReadBytes((int)this.DataSize.GetUint(this.DataSize));
                if (Utilities.IsEditorsTableHeader(dataarray))
                {
                    this.Data = dataarray;
                    Context.IsNextEditorTable = true;
                }
                else if (Utilities.IsZIPFileHeaderMatch(dataarray, Utilities.LocalFileHeader) || Utilities.IsZIPFileHeaderMatch(dataarray, Utilities.CentralDirectoryHeader))
                {
                    s.Position -= (int)this.DataSize.GetUint(this.DataSize);
                    long startPostion = s.Position;
                    this.Data = new ZIPFileStructure();
                    ((ZIPFileStructure)this.Data).Parse(s);
                    if (s.Position - startPostion > (long)this.DataSize.GetUint(this.DataSize))
                    {
                        this.Data = dataarray;
                    }
                    s.Position = startPostion + (long)this.DataSize.GetUint(this.DataSize);
                }
                else if (Utilities.IsPNGHeader(dataarray))
                {
                    this.Data = dataarray;
                }
                else if (Context.IsNextEditorTable)
                {
                    this.Data = Utilities.ReadEditorsTable(dataarray);
                    Context.IsNextEditorTable = false;
                }
                else
                {
                    s.Position = startPosition;
                    if (partitionId == 4)
                    {
                        this.JCID = new JCID();
                        this.JCID.Parse(s);
                        this.Data = null;
                    }
                    else if (partitionId == 1)
                    {
                        this.ObjectSpaceObjectPropSet = new ObjectSpaceObjectPropSet();
                        this.ObjectSpaceObjectPropSet.Parse(s);
                        this.Data = null;
                    }
                    else
                    {
                        this.Data = dataarray;
                    }
                }

            }
        }
    }
}
