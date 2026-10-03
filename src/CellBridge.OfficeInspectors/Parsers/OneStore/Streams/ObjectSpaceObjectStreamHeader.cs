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
    /// This class is used to represent a ObjectSpaceObjectStreamHeader.
    /// </summary>
    public class ObjectSpaceObjectStreamHeader : BaseStructure
    {
        /// <summary>
        /// Gets or sets an unsigned integer that specifies the number of CompactID structures.
        /// </summary>
        [BitAttribute(16)]
        public uint Count;

        /// <summary>
        /// Gets or sets the Reserved field.
        /// </summary>
        [BitAttribute(6)]
        public byte Reserved;

        /// <summary>
        /// Gets or sets the ExtendedStreamsPresent field.
        /// </summary>
        [BitAttribute(1)]
        public byte A;

        /// <summary>
        /// Gets or sets the OsidStreamNotPresent field.
        /// </summary>
        [BitAttribute(1)]
        public byte B;

        /// <summary>
        /// Parse the ObjectSpaceObjectStreamHeader structure.
        /// </summary>
        /// <param name="s">A stream containing ObjectSpaceObjectStreamHeader structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            int index = 0;
            uint temp = ReadUint();
            this.Count = (ushort)GetBits(temp, index, 24);
            index = index + 24;
            this.Reserved=(byte)GetBits(temp, index, 6);
            index = index + 6;
            this.A = (byte)GetBits(temp, index, 1);
            index = index + 1;
            this.B = (byte)GetBits(temp, index, 1);
        }
    }
}
