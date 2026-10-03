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
    /// This class is used to represent a ObjectSpaceObjectStreamOfOSIDs.
    /// </summary>
    public class ObjectSpaceObjectStreamOfOSIDs : BaseStructure
    {
        /// <summary>
        /// Gets or sets the value of header field.
        /// </summary>
        public ObjectSpaceObjectStreamHeader Header;

        /// <summary>
        /// Gets or sets the value of body field.
        /// </summary>
        public CompactID[] Body;

        /// <summary>
        /// Parse the ObjectSpaceObjectStreamOfOSIDs structure.
        /// </summary>
        /// <param name="s">A stream containing ObjectSpaceObjectStreamOfOSIDs structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.Header = new ObjectSpaceObjectStreamHeader();
            this.Header.Parse(s);
            List<CompactID> tempCompactIDList = new List<CompactID>();
            if (this.Header.Count > 0)
            {
                ulong tempCompactIDCount = this.Header.Count;
                do
                {
                    CompactID tempCompactID = new CompactID();
                    tempCompactID.Parse(s);
                    tempCompactIDList.Add(tempCompactID);
                    tempCompactIDCount--;
                } while (tempCompactIDCount > 0);
                this.Body = tempCompactIDList.ToArray();
            }
        }
    }
}
