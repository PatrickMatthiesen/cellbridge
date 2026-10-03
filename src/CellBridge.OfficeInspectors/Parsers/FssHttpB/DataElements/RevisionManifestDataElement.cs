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
    /// 2.2.1.12.5	Revision Manifest Data Elements
    /// </summary>
    public class RevisionManifestDataElement : BaseStructure
    {
        public StreamObjectHeader DataElementStart;
        public ExtendedGUID DataElementExtendedGUID;
        public SerialNumber SerialNumber;
        public CompactUnsigned64bitInteger DataElementType;
        public bit16StreamObjectHeaderStart RevisionManifest;
        public ExtendedGUID RevisionID;
        public ExtendedGUID BaseRevisionID;
        public object[] RevisionManifestDataElementsData;
        public bit8StreamObjectHeaderEnd DataElementEnd;

        /// <summary>
        /// Parse the RevisionManifestDataElement structure.
        /// </summary>
        /// <param name="s">A stream containing RevisionManifestDataElement structure.</param>
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
            this.RevisionManifest = new bit16StreamObjectHeaderStart();
            this.RevisionManifest.Parse(s);
            this.RevisionID = new ExtendedGUID();
            this.RevisionID = this.RevisionID.TryParse(s);
            this.BaseRevisionID = new ExtendedGUID();
            this.BaseRevisionID = this.BaseRevisionID.TryParse(s);
            int RevisionType = (CurrentByte() >> 3) & 0x3F;
            List<object> DataList = new List<object>();
            while ((CurrentByte() & 0x03) == 0x0 && (RevisionType == 0x0A || RevisionType == 0x19))
            {
                switch (RevisionType)
                {
                    case 0x0A:
                        {
                            RevisionManifestRootDeclareValues RootDeclareValue = new RevisionManifestRootDeclareValues();
                            RootDeclareValue.Parse(s);
                            DataList.Add(RootDeclareValue);
                            break;
                        }
                    case 0x19:
                        {
                            RevisionManifestObjectGroupReferencesValues ObjectGroupReferencesValue = new RevisionManifestObjectGroupReferencesValues();
                            ObjectGroupReferencesValue.Parse(s);
                            DataList.Add(ObjectGroupReferencesValue);
                            break;
                        }
                    default:
                        throw new Exception("The RevisionType is not right.");
                }
                RevisionType = (CurrentByte() >> 3) & 0x3F;
            }
            this.RevisionManifestDataElementsData = DataList.ToArray();
            this.DataElementEnd = new bit8StreamObjectHeaderEnd();
            this.DataElementEnd.Parse(s);
            if (this.DataElementEnd.Type != StreamObjectTypeHeaderEnd.DataElement)
                throw new InvalidDataException("Expected DataElement end header.");
        }

        /// <summary>
        /// Parse the RevisionManifestDataElement structure for ONESTORE messsage.
        /// </summary>
        /// <param name="s">A stream containing RevisionManifestDataElement structure.</param>
        /// <param name="is2ndParse">A bool value specify it is 2nd parse for onestore message.</param>
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
            this.RevisionManifest = new bit16StreamObjectHeaderStart();
            this.RevisionManifest.Parse(s);
            this.RevisionID = new ExtendedGUID();
            this.RevisionID = this.RevisionID.TryParse(s);
            this.BaseRevisionID = new ExtendedGUID();
            this.BaseRevisionID = this.BaseRevisionID.TryParse(s);
            // A flag specify it is Encryption message for ONESTORE protocol.
            bool isEncryption = false;
            int RevisionType = (CurrentByte() >> 3) & 0x3F;
            List<object> DataList = new List<object>();
            while ((CurrentByte() & 0x03) == 0x0 && (RevisionType == 0x0A || RevisionType == 0x19))
            {
                switch (RevisionType)
                {
                    case 0x0A:
                        {
                            RevisionManifestRootDeclareValues RootDeclareValue = new RevisionManifestRootDeclareValues();
                            RootDeclareValue.Parse(s);
                            // If it is the first time parse  for ONESTORE message.
                            if (!is2ndParse)
                            {
                                if (RootDeclareValue.RootExtendedGUID.GetGUID(RootDeclareValue.RootExtendedGUID).ToString() == "4A3717F8-1C14-49E7-9526-81D942DE1741".ToLower()
                                && RootDeclareValue.RootExtendedGUID.GetValue(RootDeclareValue.RootExtendedGUID) == 3)
                                {
                                    if (!isEncryption)
                                    {
                                        isEncryption = true;
                                    }
                                }
                            }

                            DataList.Add(RootDeclareValue);
                            break;
                        }
                    case 0x19:
                        {
                            RevisionManifestObjectGroupReferencesValues ObjectGroupReferencesValue = new RevisionManifestObjectGroupReferencesValues();
                            ObjectGroupReferencesValue.Parse(s);
                            if (!is2ndParse)
                            {
                                if (isEncryption)
                                {
                                    if (!Context.EncryptedObjectGroupIds.Contains(ObjectGroupReferencesValue.ObjectGroupExtendedGUID))
                                    {
                                        Context.EncryptedObjectGroupIds.Add(ObjectGroupReferencesValue.ObjectGroupExtendedGUID);
                                    }
                                }
                            }
                            DataList.Add(ObjectGroupReferencesValue);
                            break;
                        }
                    default:
                        throw new Exception("The RevisionType is not right.");
                }
                RevisionType = (CurrentByte() >> 3) & 0x3F;
            }
            this.RevisionManifestDataElementsData = DataList.ToArray();
            this.DataElementEnd = new bit8StreamObjectHeaderEnd();
            this.DataElementEnd.Parse(s);
            if (this.DataElementEnd.Type != StreamObjectTypeHeaderEnd.DataElement)
                throw new InvalidDataException("Expected DataElement end header.");
        }
    }
}
