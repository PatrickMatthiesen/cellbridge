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
    /// Section 2.2.3.2   Response Error
    /// </summary>
    public class ResponseError : BaseStructure
    {
        public bit32StreamObjectHeaderStart ErrorStart;
        public Guid ErrorTypeGUID;
        public object ErrorData;
        public bit32StreamObjectHeaderStart ErrorStringSupplementalInfoStart;
        public StringItem ErrorStringSupplementalInfo;
        public ResponseError ChainedError;
        public bit16StreamObjectHeaderEnd ErrorEnd;

        /// <summary>
        /// Parse the ResponseError structure.
        /// </summary>
        /// <param name="s">A stream containing ResponseError structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.ErrorStart = new bit32StreamObjectHeaderStart();
            this.ErrorStart.Parse(s);
            this.ErrorTypeGUID = ReadGuid();
            if (this.ErrorTypeGUID.ToString().ToUpper() == "5A66A756-87CE-4290-A38B-C61C5BA05A67")
            {
                this.ErrorData = new CellError();
                ((CellError)this.ErrorData).Parse(s);
            }
            else if (this.ErrorTypeGUID.ToString().ToUpper() == "7AFEAEBF-033D-4828-9C31-3977AFE58249")
            {
                this.ErrorData = new ProtocolError();
                ((ProtocolError)this.ErrorData).Parse(s);
            }
            else if (this.ErrorTypeGUID.ToString().ToUpper() == "32C39011-6E39-46C4-AB78-DB41929D679E")
            {
                this.ErrorData = new Win32Error();
                ((Win32Error)this.ErrorData).Parse(s);
            }
            else if (this.ErrorTypeGUID.ToString().ToUpper() == "8454C8F2-E401-405A-A198-A10B6991B56E")
            {
                this.ErrorData = new HRESULTError();
                ((HRESULTError)this.ErrorData).Parse(s);
            }

            if (ContainsStreamObjectStart32BitHeader(0x04E))
            {
                this.ErrorStringSupplementalInfoStart = new bit32StreamObjectHeaderStart();
                this.ErrorStringSupplementalInfoStart.Parse(s);
                this.ErrorStringSupplementalInfo = new StringItem();
                this.ErrorStringSupplementalInfo.Parse(s);
            }

            if (ContainsStreamObjectStart32BitHeader(0x04D))
            {
                this.ChainedError = new ResponseError();
                this.ChainedError.Parse(s);
            }
            this.ErrorEnd = new bit16StreamObjectHeaderEnd();
            this.ErrorEnd.Parse(s);
            if (this.ErrorEnd.Type != StreamObjectTypeHeaderEnd.Error)
                throw new InvalidDataException("Expected Error end header.");
        }
    }
}
