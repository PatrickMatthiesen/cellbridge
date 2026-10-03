#nullable disable
// Ported from OfficeDev/Office-Inspectors-for-Fiddler. See NOTICE.md in this project.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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



    /// <summary>
    /// 2.6.14 This class is used to represent a JCID
    /// </summary>
    public class JCID : BaseStructure
    {
        /// <summary>
        /// Gets or sets an unsigned integer that specifies the type of object
        /// </summary>
        public ushort Index;

        /// <summary>
        /// Gets or sets the IsBinary value that specifies whether the object contains encryption data transmitted over the File Synchronization via SOAP over HTTP Protocol.
        /// </summary>
        [BitAttribute(1)]
        public byte A;

        /// <summary>
        /// Gets or sets the IsPropertySet value that specifies whether the object contains a property set.
        /// </summary>
        [BitAttribute(1)]
        public byte B;

        /// <summary>
        /// Gets or sets a value of IsGraphNode field.
        /// </summary>
        [BitAttribute(1)]
        public byte C;

        /// <summary>
        /// Gets or sets the IsFileData value that specifies whether the object is a file data object.
        /// </summary>
        [BitAttribute(1)]
        public byte D;

        /// <summary>
        /// Gets or sets the IsReadOnly value that specifies whether the object's data MUST NOT be changed when the object is revised.
        /// </summary>
        [BitAttribute(1)]
        public byte E;

        /// <summary>
        /// Gets or sets the value of Reserved field.
        /// </summary>
        [BitAttribute(11)]
        public ushort Reserved;

        /// <summary>
        /// Parse the JCID structure.
        /// </summary>
        /// <param name="s">A stream containing JCID structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            int index = 0;
            uint temp = ReadUint();
            this.Index = (ushort)GetBits(temp, index, 16);
            index = index + 16;
            this.A = (byte)GetBits(temp, index, 1);
            index = index + 1;
            this.B = (byte)GetBits(temp, index, 1);
            index = index + 1;
            this.C = (byte)GetBits(temp, index, 1);
            index = index + 1;
            this.D = (byte)GetBits(temp, index, 1);
            index = index + 1;
            this.E = (byte)GetBits(temp, index, 1);
            index = index + 1;
            this.Reserved = (ushort)GetBits(temp, index, 11);
        }
    }
}
