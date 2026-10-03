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
    /// 2.2.1.12	Data Element Package
    /// </summary>
    public class DataElementPackage : BaseStructure
    {
        public StreamObjectHeader DataElementPackageStart;
        public byte Reserved;
        public object[] DataElements;
        public bit8StreamObjectHeaderEnd DataElementPackageEnd;

        /// <summary>
        /// Parse the DataElementPackage structure.
        /// </summary>
        /// <param name="s">A stream containing DataElementPackage structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.DataElementPackageStart = new StreamObjectHeader().TryParse(s);
            this.Reserved = ReadByte();

            long DataElementPackageType = PreReadDataElementPackageType();
            List<object> DataElementsList = new List<object>();

            while (ContainsStreamObjectHeader(0x01) && (DataElementPackageType == 0x01 || DataElementPackageType == 0x02 ||
            DataElementPackageType == 0x03 || DataElementPackageType == 0x04 || DataElementPackageType == 0x05 ||
            DataElementPackageType == 0x06 || DataElementPackageType == 0x0A))
            {
                    switch (DataElementPackageType)
                    {
                        case 0x01:
                            {
                                StorageIndexDataElement StorageIndex = new StorageIndexDataElement();
                                StorageIndex.Parse(s);
                                DataElementsList.Add(StorageIndex);
                                break;
                            }
                        case 0x02:
                            {
                                StorageManifestDataElement StorageManifest = new StorageManifestDataElement();
                                StorageManifest.Parse(s);
                                DataElementsList.Add(StorageManifest);
                                break;
                            }
                        case 0x03:
                            {
                                CellManifestDataElement CellManifest = new CellManifestDataElement();
                                CellManifest.Parse(s);
                                DataElementsList.Add(CellManifest);
                                break;
                            }
                        case 0x04:
                            {
                                RevisionManifestDataElement RevisionManifest = new RevisionManifestDataElement();
                                RevisionManifest.Parse(s);
                                DataElementsList.Add(RevisionManifest);
                                break;
                            }
                        case 0x05:
                            {
                                ObjectGroupDataElements ObjectGroup = new ObjectGroupDataElements();
                                ObjectGroup.Parse(s);
                                DataElementsList.Add(ObjectGroup);
                                break;
                            }
                        case 0x06:
                            {
                                DataElementFragmentDataElement DataElementFragment = new DataElementFragmentDataElement();
                                DataElementFragment.Parse(s);
                                DataElementsList.Add(DataElementFragment);
                                break;
                            }
                        case 0x0A:
                            {
                                ObjectDataBLOBDataElements ObjectDataBLOB = new ObjectDataBLOBDataElements();
                                ObjectDataBLOB.Parse(s);
                                DataElementsList.Add(ObjectDataBLOB);
                                break;
                            }
                        default:
                            throw new Exception("The DataElementPackageType is not right.");
                    }
                    DataElementPackageType = PreReadDataElementPackageType();
            }

            this.DataElements = DataElementsList.ToArray();
            this.DataElementPackageEnd = new bit8StreamObjectHeaderEnd();
            this.DataElementPackageEnd.Parse(s);
            if (this.DataElementPackageEnd.Type != StreamObjectTypeHeaderEnd.DataElementPackage)
                throw new InvalidDataException("Expected DataElementPackage end header.");
        }

        /// <summary>
        /// Parse the DataElementPackage structure
        /// </summary>
        /// <param name="s">A stream containing DataElementPackage structure.</param>
        /// <param name="is2ndParse">A bool value specify it is 2nd parse for onestore message.</param>
        public override void Parse(Stream s,  bool is2ndParse)
        {
            base.Parse(s);
            this.DataElementPackageStart = new StreamObjectHeader().TryParse(s);
            this.Reserved = ReadByte();

            long DataElementPackageType = PreReadDataElementPackageType();
            List<object> DataElementsList = new List<object>();

            while (ContainsStreamObjectHeader(0x01) && (DataElementPackageType == 0x01 || DataElementPackageType == 0x02 ||
            DataElementPackageType == 0x03 || DataElementPackageType == 0x04 || DataElementPackageType == 0x05 ||
            DataElementPackageType == 0x06 || DataElementPackageType == 0x0A))
            {
                switch (DataElementPackageType)
                {
                    case 0x01:
                        {
                            StorageIndexDataElement StorageIndex = new StorageIndexDataElement();
                            StorageIndex.Parse(s);
                            DataElementsList.Add(StorageIndex);
                            break;
                        }
                    case 0x02:
                        {
                            StorageManifestDataElement StorageManifest = new StorageManifestDataElement();
                            StorageManifest.Parse(s);
                            DataElementsList.Add(StorageManifest);
                            break;
                        }
                    case 0x03:
                        {
                            CellManifestDataElement CellManifest = new CellManifestDataElement();
                            CellManifest.Parse(s);
                            DataElementsList.Add(CellManifest);
                            break;
                        }
                    case 0x04:
                        {
                            RevisionManifestDataElement RevisionManifest = new RevisionManifestDataElement();
                            if (Context.IsOneStore)
                            {
                                RevisionManifest.Parse(s, is2ndParse);
                            }
                            else
                            {
                                RevisionManifest.Parse(s);
                            }
                            DataElementsList.Add(RevisionManifest);
                            break;
                        }
                    case 0x05:
                        {
                            ObjectGroupDataElements ObjectGroup = new ObjectGroupDataElements();
                            //Parse ObjectGroupDataElements for ONESTORE message.
                            ObjectGroup.Parse(s, is2ndParse);
                            DataElementsList.Add(ObjectGroup);
                            break;
                        }
                    case 0x06:
                        {
                            DataElementFragmentDataElement DataElementFragment = new DataElementFragmentDataElement();
                            DataElementFragment.Parse(s);
                            DataElementsList.Add(DataElementFragment);
                            break;
                        }
                    case 0x0A:
                        {
                            ObjectDataBLOBDataElements ObjectDataBLOB = new ObjectDataBLOBDataElements();
                            ObjectDataBLOB.Parse(s);
                            DataElementsList.Add(ObjectDataBLOB);
                            break;
                        }
                    default:
                        throw new Exception("The DataElementPackageType is not right.");
                }
                DataElementPackageType = PreReadDataElementPackageType();
            }

            this.DataElements = DataElementsList.ToArray();
            this.DataElementPackageEnd = new bit8StreamObjectHeaderEnd();
            this.DataElementPackageEnd.Parse(s);
            if (this.DataElementPackageEnd.Type != StreamObjectTypeHeaderEnd.DataElementPackage)
                throw new InvalidDataException("Expected DataElementPackage end header.");
        }
    }
}
