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
    /// This class is used to represent a PropertyID.
    /// </summary>
    public class PropertyID:BaseStructure
    {
        /// <summary>
        /// Gets or sets the value of id field.
        /// </summary>
        [BitAttribute(18)]
        public uint Id;

        /// <summary>
        /// Gets or sets the value of type field.
        /// </summary>
        [BitAttribute(5)]
        public byte Type;

        /// <summary>
        /// Gets or sets the value of boolValue field.
        /// </summary>
        [BitAttribute(1)]
        public byte BoolValue;

        /// <summary>
        /// Parse the PropertyID structure.
        /// </summary>
        /// <param name="s">A stream containing JCID structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            int index = 0;
            uint temp = ReadUint();
            this.Id= (uint)GetBits(temp, index, 26);
            index += 26;
            this.Type= (byte)GetBits(temp, index, 5);
            index += 5;
            this.BoolValue= (byte)GetBits(temp, index, 1);
        }
    }
}
