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
    /// 2.2.1.12.6.3	Object Metadata Declaration
    /// </summary>
    public class ObjectMetadataDeclaration : BaseStructure
    {
        public bit32StreamObjectHeaderStart ObjectGroupMetadataDeclarations;
        public ObjectMetadata[] ObjectMetadata;
        public bit16StreamObjectHeaderEnd ObjectGroupMetadataDeclarationsEnd;

        /// <summary>
        /// Parse the ObjectMetadataDeclaration structure.
        /// </summary>
        /// <param name="s">A stream containing ObjectMetadataDeclaration structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.ObjectGroupMetadataDeclarations = new bit32StreamObjectHeaderStart();
            this.ObjectGroupMetadataDeclarations.Parse(s);
            List<ObjectMetadata> ObjectMetadataList = new List<ObjectMetadata>();
            while (ContainsStreamObjectStart32BitHeader(0x78))
            {
                ObjectMetadata tempObjectMetadata = new ObjectMetadata();
                tempObjectMetadata.Parse(s);
                ObjectMetadataList.Add(tempObjectMetadata);
            }
            this.ObjectMetadata = ObjectMetadataList.ToArray();
            this.ObjectGroupMetadataDeclarationsEnd = new bit16StreamObjectHeaderEnd();
            this.ObjectGroupMetadataDeclarationsEnd.Parse(s);
            if (this.ObjectGroupMetadataDeclarationsEnd.Type != StreamObjectTypeHeaderEnd.ObjectGroupMetadataDeclarations)
                throw new InvalidDataException("Expected ObjectGroupMetadataDeclarations end header.");
        }
    }
}
