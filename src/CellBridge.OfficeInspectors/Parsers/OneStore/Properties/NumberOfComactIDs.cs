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
    /// The class is used to represent the number of the array.
    /// </summary>
    public class NumberOfComactIDs : BaseStructure
    {
        /// <summary>
        /// Gets or sets the number of array.
        /// </summary>
        public uint Number;
        /// <summary>
        /// A stream containing ArrayNumber structure.
        /// </summary>
        /// <param name="s">A stream containing JCID structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.Number = ReadUint();
        }

    }
}
