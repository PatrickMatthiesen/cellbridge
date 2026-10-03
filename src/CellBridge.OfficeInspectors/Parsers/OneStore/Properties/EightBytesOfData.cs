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
    /// This class is used to represent the property contains 8 bytes of data in the PropertySet.rgData stream field.
    /// </summary>
    public class EightBytesOfData : BaseStructure
    {
        /// <summary>
        /// Gets or sets the data of property.
        /// </summary>
        public byte[] Data;

        /// <summary>
        /// Parse the EightBytesOfData structure.
        /// </summary>
        /// <param name="s">A stream containing EightBytesOfData structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.Data = new byte[8];
            this.Data = ReadBytes(8);
        }
    }
}
