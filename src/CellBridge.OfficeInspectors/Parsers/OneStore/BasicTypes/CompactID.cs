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
    /// This class is used to represent the CompactID structrue.
    /// </summary>
    public class CompactID:BaseStructure
    {
        /// <summary>
        /// Gets or sets an unsigned integer that specifies the value of the ExtendedGUID.n field.
        /// </summary>
        public byte N;

        /// <summary>
        /// Gets or sets an unsigned integer that specifies the index in the global identification table.
        /// </summary>
        [BitAttribute(16)]
        public uint GuidIndex;

        /// <summary>
        /// Parse the CompactID structure.
        /// </summary>
        /// <param name="s">A stream containing CompactID structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            int index = 0;
            uint temp = ReadUint();
            this.N = (byte)GetBits(temp, index, 8);
            index = index + 8;
            this.GuidIndex = (uint)GetBits(temp, index, 24);
        }
    }
}
