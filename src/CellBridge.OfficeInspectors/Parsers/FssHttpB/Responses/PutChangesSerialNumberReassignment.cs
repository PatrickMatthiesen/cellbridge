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

    /// <summary>OfficeDev-compatible Put Changes serial reassignment record.</summary>
    public class PutChangesSerialNumberReassignment : BaseStructure
    {
        public bit32StreamObjectHeaderStart Header;
        public ExtendedGUID DataElementId;
        public SerialNumber SerialNumber;

        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.Header = new bit32StreamObjectHeaderStart();
            this.Header.Parse(s);
            if (this.Header.B != 0 ||
                (this.Header.Type != StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassignAll &&
                 this.Header.Type != StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassign))
                throw new InvalidDataException("Invalid Put Changes serial reassignment header.");
            ulong length = this.Header.LargeLength is { } largeLength
                ? largeLength.GetUint(largeLength) : (ulong)this.Header.Length;
            if (length == 0 || length > (ulong)(s.Length - s.Position))
                throw new InvalidDataException("Invalid Put Changes serial reassignment length.");
            long end = s.Position + (long)length;
            if (this.Header.Type == StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassign)
                this.DataElementId = new ExtendedGUID().TryParse(s);
            this.SerialNumber = new SerialNumber().TryParse(s);
            if (this.SerialNumber is not SerialNumberNullValue && this.SerialNumber is not SerialNumber64BitUintValue)
                throw new InvalidDataException("Invalid reassigned serial number.");
            if (s.Position != end)
                throw new InvalidDataException("Put Changes serial reassignment does not match its declared length.");
        }
    }
}
