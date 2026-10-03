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
    /// 2.2.3.1.1	Query Access
    /// </summary>
    public class QueryAccessResponse : BaseStructure
    {
        public bit32StreamObjectHeaderStart ReadAccessResponseStart;
        public ResponseError ReadAccessResponseError;
        public bit16StreamObjectHeaderEnd ReadAccessResponseEnd;
        public bit32StreamObjectHeaderStart WriteAccessResponseStart;
        public ResponseError WriteAccessResponseError;
        public bit16StreamObjectHeaderEnd WriteAccessResponseEnd;
        public OpaqueStreamObject[] AdditionalAccessResponses;

        /// <summary>
        /// Parse the QueryAccessResponse structure.
        /// </summary>
        /// <param name="s">A stream containing QueryAccessResponse structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.ReadAccessResponseStart = new bit32StreamObjectHeaderStart();
            this.ReadAccessResponseStart.Parse(s);
            this.ReadAccessResponseError = new ResponseError();
            this.ReadAccessResponseError.Parse(s);
            this.ReadAccessResponseEnd = new bit16StreamObjectHeaderEnd();
            this.ReadAccessResponseEnd.Parse(s);
            if (this.ReadAccessResponseEnd.Type != StreamObjectTypeHeaderEnd.ReadAccessResponse)
                throw new InvalidDataException("Expected ReadAccessResponse end header.");
            this.WriteAccessResponseStart = new bit32StreamObjectHeaderStart();
            this.WriteAccessResponseStart.Parse(s);
            this.WriteAccessResponseError = new ResponseError();
            this.WriteAccessResponseError.Parse(s);
            this.WriteAccessResponseEnd = new bit16StreamObjectHeaderEnd();
            this.WriteAccessResponseEnd.Parse(s);
            if (this.WriteAccessResponseEnd.Type != StreamObjectTypeHeaderEnd.WriteAccessResponse)
                throw new InvalidDataException("Expected WriteAccessResponse end header.");
            var additional = new List<OpaqueStreamObject>();
            while (!ContainsStreamObjectEndHeader((ushort)StreamObjectTypeHeaderEnd.SubResponse))
            {
                var item = new OpaqueStreamObject();
                item.Parse(s);
                additional.Add(item);
            }
            this.AdditionalAccessResponses = additional.ToArray();
        }
    }
}
