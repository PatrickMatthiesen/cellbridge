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
    /// The class is used to represent the prtArrayOfPropertyValues .
    /// </summary>
    public class PrtArrayOfPropertyValues : BaseStructure
    {
        /// <summary>
        /// Gets or sets an unsigned integer that specifies the number of properties in Data.
        /// </summary>
        public uint CProperties;

        /// <summary>
        /// Gets or sets the value of prid field.
        /// </summary>
        public PropertyID Prid;

        /// <summary>
        /// Gets or sets the value of Data field.
        /// </summary>
        public PropertySet[] Data;

        /// <summary>
        /// This method is used to deserialize the prtArrayOfPropertyValues from the specified byte array and start index.
        /// </summary>
        /// <param name="s">A stream containing PrtArrayOfPropertyValues structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.CProperties = ReadUint();
            if (this.CProperties> 0)
            {
                this.Prid = new PropertyID();
                this.Prid.Parse(s);
                this.Data = new PropertySet[this.CProperties];
                List<PropertySet> tempDataList = new List<PropertySet>();
                for (int i = 0; i < this.CProperties; i++)
                {
                    this.Data[i] = new PropertySet();
                    this.Data[i].Parse(s);
                }
            }
        }
    }
}
