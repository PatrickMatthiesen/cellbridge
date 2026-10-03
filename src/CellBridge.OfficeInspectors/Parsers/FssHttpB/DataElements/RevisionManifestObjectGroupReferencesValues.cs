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
    /// This type is defined for section 2.2.1.12.5 Revision Manifest Data Element of object group references values
    /// </summary>
    public class RevisionManifestObjectGroupReferencesValues : BaseStructure
    {
        public bit16StreamObjectHeaderStart RevisionManifestObjectGroupReferences;
        public ExtendedGUID ObjectGroupExtendedGUID;

        /// <summary>
        /// Parse the RevisionManifestObjectGroupReferencesValues structure.
        /// </summary>
        /// <param name="s">A stream containing RevisionManifestObjectGroupReferencesValues structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.RevisionManifestObjectGroupReferences = new bit16StreamObjectHeaderStart();
            this.RevisionManifestObjectGroupReferences.Parse(s);
            this.ObjectGroupExtendedGUID = new ExtendedGUID();
            this.ObjectGroupExtendedGUID = this.ObjectGroupExtendedGUID.TryParse(s);
        }
    }
}
