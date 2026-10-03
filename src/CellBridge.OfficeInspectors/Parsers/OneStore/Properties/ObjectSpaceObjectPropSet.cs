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
    /// This class is used to represent a ObjectSpaceObjectPropSet.
    /// </summary>
    public class ObjectSpaceObjectPropSet:BaseStructure
    {
        /// <summary>
        /// Gets or sets an ObjectSpaceObjectStreamOfOIDs that specifies the count and list of objects that are referenced by this ObjectSpaceObjectPropSet.
        /// </summary>
        public ObjectSpaceObjectStreamOfOIDs OIDs;
        /// <summary>
        /// Gets or sets The value of OSIDs.
        /// </summary>
        public ObjectSpaceObjectStreamOfOSIDs OSIDs;
        /// Gets or sets the value of ContextIDs field.
        /// </summary>
        public ObjectSpaceObjectStreamOfContextIDs ContextIDs;
        /// <summary>
        /// Gets or sets the value of body field.
        /// </summary>
        public PropertySet Body;
        /// <summary>
        /// Gets or sets the value of padding field.
        /// </summary>
        public byte[] Padding;
        /// <summary>
        /// Parse the ObjectSpaceObjectPropSet structure.
        /// </summary>
        /// <param name="s">A stream containing ObjectSpaceObjectPropSet structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            long startIndex = s.Position;
            this.OIDs = new ObjectSpaceObjectStreamOfOIDs();
            this.OIDs.Parse(s);
            if (this.OIDs.Header.B==0)
            {
                this.OSIDs = new ObjectSpaceObjectStreamOfOSIDs();
                this.OSIDs.Parse(s);
            }
            if (this.OIDs.Header.B == 0 && this.OSIDs.Header.A == 1)
            {
                this.ContextIDs = new ObjectSpaceObjectStreamOfContextIDs();
                this.ContextIDs.Parse(s);
            }

            this.Body = new PropertySet();
            this.Body.Parse(s);

            long paddingLen = 8-(s.Position -startIndex)%8;
            if (paddingLen < 8)
            {
                this.Padding = new byte[paddingLen];
                for (int i = 0; i < paddingLen; i++)
                {
                    byte temp = ReadByte();
                    //The size of the padding field is the number of bytes necessary to ensure the total size of ObjectSpaceObjectPropSet structure is a multiple of 8.
                    //If the byte read in sequence for padding is 0, then assign to Padding field.
                    if (temp==0)
                    {
                        this.Padding[i] = temp;
                    }
                    // If the byte read in sequence for padding is not 0, then assgin 0 to Padding field. s.Position backward 1.
                    else
                    {
                        this.Padding[i] = 0;
                        s.Position -= 1;
                    }
                }
            }
        }
    }
}
