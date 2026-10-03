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
    /// 2.2.1.12.6	Object Group Data Elements
    /// </summary>
    public class ObjectGroupDataElements : BaseStructure
    {
        public StreamObjectHeader DataElementStart;
        public ExtendedGUID DataElementExtendedGUID;
        public SerialNumber SerialNumber;
        public CompactUnsigned64bitInteger DataElementType;
        public DataElementHash DataElementHash;
        public StreamObjectHeader ObjectGroupDeclarationsStart;
        public object[] ObjectDeclarationOrObjectDataBLOBDeclaration;
        public bit8StreamObjectHeaderEnd ObjectGroupDeclarationsEnd;
        public ObjectMetadataDeclaration ObjectMetadataDeclaration;
        public StreamObjectHeader ObjectGroupDataStart;
        public object[] ObjectDataOrObjectDataBLOBReference;
        public bit8StreamObjectHeaderEnd ObjectGroupDataEnd;
        public bit8StreamObjectHeaderEnd DataElementEnd;

        /// <summary>
        /// Parse the ObjectGroupDataElements structure.
        /// </summary>
        /// <param name="s">A stream containing ObjectGroupDataElements structure.</param>
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

            if (ContainsStreamObjectHeader(0x06))
            {
                this.DataElementHash = new DataElementHash();
                this.DataElementHash.Parse(s);
            }
            this.ObjectGroupDeclarationsStart = new StreamObjectHeader();
            this.ObjectGroupDeclarationsStart = this.ObjectGroupDeclarationsStart.TryParse(s);
            List<object> DeclarationList = new List<object>();
            while (ContainsStreamObjectHeader(0x18) || ContainsStreamObjectHeader(0x05))
            {
                if (ContainsStreamObjectHeader(0x18))
                {
                    ObjectDeclaration Declaration = new ObjectDeclaration();
                    Declaration.Parse(s);
                    DeclarationList.Add(Declaration);
                }
                else if (ContainsStreamObjectHeader(0x05))
                {
                    ObjectDataBLOBDeclaration DeclarationBLOB = new ObjectDataBLOBDeclaration();
                    DeclarationBLOB.Parse(s);
                    DeclarationList.Add(DeclarationBLOB);
                }
            }
            this.ObjectDeclarationOrObjectDataBLOBDeclaration = DeclarationList.ToArray();
            this.ObjectGroupDeclarationsEnd = new bit8StreamObjectHeaderEnd();
            this.ObjectGroupDeclarationsEnd.Parse(s);
            if (this.ObjectGroupDeclarationsEnd.Type != StreamObjectTypeHeaderEnd.ObjectGroupDeclarations)
                throw new InvalidDataException("Expected ObjectGroupDeclarations end header.");

            if (ContainsStreamObjectStart32BitHeader(0x79))
            {
                this.ObjectMetadataDeclaration = new ObjectMetadataDeclaration();
                this.ObjectMetadataDeclaration.Parse(s);
            }

            this.ObjectGroupDataStart = new StreamObjectHeader();
            this.ObjectGroupDataStart = this.ObjectGroupDataStart.TryParse(s);
            List<object> ObjectDataList = new List<object>();
            Context.IsNextEditorTable = false;
            while (ContainsStreamObjectHeader(0x16) || ContainsStreamObjectHeader(0x1C))
            {
                if (ContainsStreamObjectHeader(0x16))
                {
                    ObjectData data = new ObjectData();
                    data.Parse(s);
                    ObjectDataList.Add(data);
                }
                else if (ContainsStreamObjectHeader(0x1C))
                {
                    ObjectDataBLOBReference DataBLOB = new ObjectDataBLOBReference();
                    DataBLOB.Parse(s);
                    ObjectDataList.Add(DataBLOB);
                }
            }
            this.ObjectDataOrObjectDataBLOBReference = ObjectDataList.ToArray();
            Context.IsNextEditorTable = false;
            this.ObjectGroupDataEnd = new bit8StreamObjectHeaderEnd();
            this.ObjectGroupDataEnd.Parse(s);
            if (this.ObjectGroupDataEnd.Type != StreamObjectTypeHeaderEnd.ObjectGroupData)
                throw new InvalidDataException("Expected ObjectGroupData end header.");
            this.DataElementEnd = new bit8StreamObjectHeaderEnd();
            this.DataElementEnd.Parse(s);
            if (this.DataElementEnd.Type != StreamObjectTypeHeaderEnd.DataElement)
                throw new InvalidDataException("Expected DataElement end header.");
        }

        /// <summary>
        /// Parse the ObjectGroupDataElements structure.
        /// </summary>
        /// <param name="s">A stream containing ObjectGroupDataElements structure.</param>
        public override void Parse(Stream s, bool is2ndParse)
        {
            base.Parse(s);
            this.DataElementStart = new StreamObjectHeader().TryParse(s);
            this.DataElementExtendedGUID = new ExtendedGUID();
            this.DataElementExtendedGUID = this.DataElementExtendedGUID.TryParse(s);
            this.SerialNumber = new SerialNumber();
            this.SerialNumber = this.SerialNumber.TryParse(s);
            this.DataElementType = new CompactUnsigned64bitInteger();
            this.DataElementType = this.DataElementType.TryParse(s);

            //2.2.1.5	Stream Object Header
            //Data Element Hash	0x06
            if (ContainsStreamObjectHeader(0x06))
            {
                this.DataElementHash = new DataElementHash();
                this.DataElementHash.Parse(s);
            }
            this.ObjectGroupDeclarationsStart = new StreamObjectHeader();
            this.ObjectGroupDeclarationsStart = this.ObjectGroupDeclarationsStart.TryParse(s);
            List<object> DeclarationList = new List<object>();

            // New a list to record PartitionId in DeclarationList.
            List<ulong> PartitionIdList = new List<ulong>();

            //Object Group Object Declare	0x18
            //Object Group Object Data BLOB Declaration   0x05
            while (ContainsStreamObjectHeader(0x18) || ContainsStreamObjectHeader(0x05))
            {
                //Object Group Object Declare	0x18
                if (ContainsStreamObjectHeader(0x18))
                {
                    ObjectDeclaration Declaration = new ObjectDeclaration();
                    Declaration.Parse(s);
                    DeclarationList.Add(Declaration);

                    //Add ObjectPartitionID to a list.
                    PartitionIdList.Add(Declaration.ObjectPartitionID.GetUint(Declaration.ObjectPartitionID));
                }
                //Object Group Object Data BLOB Declaration   0x05
                else if (ContainsStreamObjectHeader(0x05))
                {
                    ObjectDataBLOBDeclaration DeclarationBLOB = new ObjectDataBLOBDeclaration();
                    DeclarationBLOB.Parse(s);
                    DeclarationList.Add(DeclarationBLOB);

                    //Add ObjectPartitionID to a list.
                    PartitionIdList.Add(DeclarationBLOB.ObjectPartitionID.GetUint(DeclarationBLOB.ObjectPartitionID));
                }
            }
            this.ObjectDeclarationOrObjectDataBLOBDeclaration = DeclarationList.ToArray();
            this.ObjectGroupDeclarationsEnd = new bit8StreamObjectHeaderEnd();
            this.ObjectGroupDeclarationsEnd.Parse(s);
            if (this.ObjectGroupDeclarationsEnd.Type != StreamObjectTypeHeaderEnd.ObjectGroupDeclarations)
                throw new InvalidDataException("Expected ObjectGroupDeclarations end header.");

            //Object Group metadata declarations	0x79
            if (ContainsStreamObjectStart32BitHeader(0x79))
            {
                this.ObjectMetadataDeclaration = new ObjectMetadataDeclaration();
                this.ObjectMetadataDeclaration.Parse(s);
            }

            this.ObjectGroupDataStart = new StreamObjectHeader();
            this.ObjectGroupDataStart = this.ObjectGroupDataStart.TryParse(s);
            List<object> ObjectDataList = new List<object>();

            Context.IsNextEditorTable = false;

            int dataIndex = 0;
            //Object Group Object Data	0x16
            //Object Group Object Data BLOB reference	0x1C
            while (ContainsStreamObjectHeader(0x16) || ContainsStreamObjectHeader(0x1C))
            {
                //Object Group Object Data	0x16
                if (ContainsStreamObjectHeader(0x16))
                {
                    ObjectData data = new ObjectData();

                    if (Context.IsOneStore)
                    {
                        if (is2ndParse)
                        {
                            //If it's encrypted ObjectGroup, only parse JCID structure when 2nd Parse for ONESTORE.
                            if ((Context.EncryptedObjectGroupIds.Where(d => d.GetGUID(d) == this.DataElementExtendedGUID.GetGUID(this.DataElementExtendedGUID))).SingleOrDefault() != null)
                            {
                                if (PartitionIdList[dataIndex] == 4)
                                {
                                    data.Parse(s, PartitionIdList[dataIndex]);
                                }
                                else
                                {
                                    data.Parse(s);
                                }

                            }
                            else
                            {
                                data.Parse(s, PartitionIdList[dataIndex]);
                            }
                        }
                        else
                        {
                            //If it's first time parse ONESTORE message, only parse JCID structure when 2nd Parse for ONESTORE.
                            if (PartitionIdList[dataIndex] == 4)
                            {
                                data.Parse(s, PartitionIdList[dataIndex]);
                            }
                            else
                            {
                                data.Parse(s);
                            }
                        }

                    }
                    else
                    {
                        data.Parse(s);
                    }

                    ObjectDataList.Add(data);
                }//Object Group Object Data BLOB reference	0x1C
                else if (ContainsStreamObjectHeader(0x1C))
                {
                    ObjectDataBLOBReference DataBLOB = new ObjectDataBLOBReference();
                    DataBLOB.Parse(s);
                    ObjectDataList.Add(DataBLOB);
                }
                dataIndex++;
            }
            this.ObjectDataOrObjectDataBLOBReference = ObjectDataList.ToArray();


            Context.IsNextEditorTable = false;
            this.ObjectGroupDataEnd = new bit8StreamObjectHeaderEnd();
            this.ObjectGroupDataEnd.Parse(s);
            if (this.ObjectGroupDataEnd.Type != StreamObjectTypeHeaderEnd.ObjectGroupData)
                throw new InvalidDataException("Expected ObjectGroupData end header.");
            this.DataElementEnd = new bit8StreamObjectHeaderEnd();
            this.DataElementEnd.Parse(s);
            if (this.DataElementEnd.Type != StreamObjectTypeHeaderEnd.DataElement)
                throw new InvalidDataException("Expected DataElement end header.");
        }
    }
}
