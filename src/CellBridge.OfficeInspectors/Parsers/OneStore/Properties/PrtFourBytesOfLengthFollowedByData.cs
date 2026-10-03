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
    /// This class is used to represent the prtFourBytesOfLengthFollowedByData.
    /// </summary>
    public class PrtFourBytesOfLengthFollowedByData : BaseStructure
    {
        /// <summary>
        /// Gets or sets an unsigned integer that specifies the size, in bytes, of the Data field.
        /// </summary>
        public uint cb;

        /// <summary>
        /// Gets or sets the value of Data field.
        /// </summary>
        public byte[] Data;

        /// <summary>
        /// Parse the PrtFourBytesOfLengthFollowedByData structure.
        /// </summary>
        /// <param name="s">A stream containing PrtFourBytesOfLengthFollowedByData structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            int index = 0;
            this.cb = ReadUint();
            index += 4;
            this.Data = new byte[this.cb];
            for (int i = 0; i < this.cb; i++)
            {
                this.Data[i] = ReadByte();
            }
        }
    }
}
