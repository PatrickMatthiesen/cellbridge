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
    /// This type is defined for section 2.2.1.12.5 Revision Manifest Data Element of root declare Values
    /// </summary>
    public class RevisionManifestRootDeclareValues : BaseStructure
    {
        public bit16StreamObjectHeaderStart RevisionManifestRootDeclare;
        public ExtendedGUID RootExtendedGUID;
        public ExtendedGUID ObjectExtendedGUID;

        /// <summary>
        /// Parse the RevisionManifestRootDeclareValues structure.
        /// </summary>
        /// <param name="s">A stream containing RevisionManifestRootDeclareValues structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.RevisionManifestRootDeclare = new bit16StreamObjectHeaderStart();
            this.RevisionManifestRootDeclare.Parse(s);
            this.RootExtendedGUID = new ExtendedGUID();
            this.RootExtendedGUID = this.RootExtendedGUID.TryParse(s);
            this.ObjectExtendedGUID = new ExtendedGUID();
            this.ObjectExtendedGUID = this.ObjectExtendedGUID.TryParse(s);
        }

    }
}
