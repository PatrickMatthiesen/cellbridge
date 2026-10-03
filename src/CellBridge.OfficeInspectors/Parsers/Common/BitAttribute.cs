#nullable disable
// Ported from OfficeDev/Office-Inspectors-for-Fiddler. See NOTICE.md in this project.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;


namespace CellBridge.OfficeInspectors.Parsers
{

    /// <summary>
    /// Custom attribute for bit length
    /// </summary>
    [AttributeUsage(AttributeTargets.All)]
    public class BitAttribute : System.Attribute
    {
        public readonly int BitLength;
        public BitAttribute(int bitLength)
        {
            this.BitLength = bitLength;
        }
    }
}
