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
    /// This class is used to represent a PropertySet.
    /// </summary>
    public class PropertySet:BaseStructure
    {
        /// <summary>
        /// Gets or sets an unsigned integer that specifies the number of properties in this PropertySet structure.
        /// </summary>
        public ushort CProperties;

        /// <summary>
        /// Gets or sets the value of rgPrids.
        /// </summary>
        public PropertyID[] RgPrids;

        /// <summary>
        /// Gets or sets the value of rgData field.
        /// </summary>
        public object[] RgData;

        /// <summary>
        /// Parse the PropertySet structure.
        /// </summary>
        /// <param name="s">A stream containing PropertySet structure.</param>
        public override void Parse(Stream s)
        {
            base.Parse(s);
            this.CProperties = ReadUshort();
            this.RgPrids = new PropertyID[this.CProperties];
            this.RgData = new object[this.CProperties];
            List<PropertyID> tempPropertyIDList = new List<PropertyID>();
            if (this.CProperties>0)
            {
                ulong tempCount = CProperties;
                do
                {
                    PropertyID tempPropertyID = new PropertyID();
                    tempPropertyID.Parse(s);
                    tempPropertyIDList.Add(tempPropertyID);
                    tempCount--;
                } while (tempCount > 0);
                this.RgPrids = tempPropertyIDList.ToArray();
            }
            List<object> tempRgDataList = new List<object>();
            if (this.RgPrids!=null)
            {
                foreach (PropertyID propertyID in this.RgPrids)
                {
                    object tempRgData =new object();
                    switch ((rgDataType)propertyID.Type)
                    {
                        case rgDataType.NoData:
                        case rgDataType.Bool:
                        case rgDataType.ObjectID:
                        case rgDataType.ContextID:
                        case rgDataType.ObjectSpaceID:
                            tempRgData = new NoData();
                            ((NoData)tempRgData).Parse(s);
                            break;
                        case rgDataType.ArrayOfObjectIDs:
                        case rgDataType.ArrayOfObjectSpaceIDs:
                        case rgDataType.ArrayOfContextIDs:
                            tempRgData = new NumberOfComactIDs();
                            ((NumberOfComactIDs)tempRgData).Parse(s);
                            break;
                        case rgDataType.OneByteOfData:
                            tempRgData = new OneByteOfData();
                            ((OneByteOfData)tempRgData).Parse(s);
                            break;
                        case rgDataType.TwoBytesOfData:
                            tempRgData = new TwoBytesOfData();
                            ((TwoBytesOfData)tempRgData).Parse(s);
                            break;
                        case rgDataType.FourBytesOfData:
                            tempRgData = new FourBytesOfData();
                            ((FourBytesOfData)tempRgData).Parse(s);
                            break;
                        case rgDataType.EightBytesOfData:
                            tempRgData = new EightBytesOfData();
                            ((EightBytesOfData)tempRgData).Parse(s);
                            break;
                        case rgDataType.FourBytesOfLengthFollowedByData:
                            tempRgData = new PrtFourBytesOfLengthFollowedByData();
                            ((PrtFourBytesOfLengthFollowedByData)tempRgData).Parse(s);
                            break;
                        case rgDataType.ArrayOfPropertyValues:
                            tempRgData = new PrtArrayOfPropertyValues();
                            ((PrtArrayOfPropertyValues)tempRgData).Parse(s);
                            break;
                        case rgDataType.PropertySet:
                            tempRgData = new PropertySet();
                            ((PropertySet)tempRgData).Parse(s);
                            break;
                        default:
                            break;
                    }
                    if (tempRgData != null)
                    {
                        tempRgDataList.Add(tempRgData);
                    }
                }
                this.RgData = tempRgDataList.ToArray();
            }
        }
    }
}
