#nullable disable
// Ported from OfficeDev/Office-Inspectors-for-Fiddler. See NOTICE.md in this project.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;


namespace CellBridge.OfficeInspectors.Parsers
{
    public abstract class BaseStructure
    {
        /// <summary>
        /// The stream to parse
        /// </summary>
        private Stream stream;

        internal InspectionContext Context { get; private set; }

        /// <summary>
        /// Parse stream to specific message
        /// </summary>
        /// <param name="s">Stream to parse</param>
        public virtual void Parse(Stream s)
        {
            ArgumentNullException.ThrowIfNull(s);
            if (!s.CanRead || !s.CanSeek)
                throw new ArgumentException("Inspection requires a readable, seekable stream.", nameof(s));
            stream = s;
            Context = InspectionContext.For(s);
        }

        /// <summary>
        /// Parse the ObjectData structure for ONESTORE message.
        /// </summary>
        /// <param name="s">A stream containing ObjectData structure.</param>
        /// <param name="partitionId">A compact unsigned 64-bit integer that specifies the object partition Id of the object.</param>
        public virtual void Parse(Stream s, ulong partitionId)
        {
            ArgumentNullException.ThrowIfNull(s);
            if (!s.CanRead || !s.CanSeek)
                throw new ArgumentException("Inspection requires a readable, seekable stream.", nameof(s));
            stream = s;
            Context = InspectionContext.For(s);
        }

        /// <summary>
        /// Parse the Data structure for ONESTORE message.
        /// </summary>
        /// <param name="s">A stream containing Data structure for ONESTORE message.</param>
        /// <param name="is2ndParse">A bool value that specifies is 2nd Parse  Data structure for ONESTORE message.</param>
        public virtual void Parse(Stream s, bool is2ndParse)
        {
            ArgumentNullException.ThrowIfNull(s);
            if (!s.CanRead || !s.CanSeek)
                throw new ArgumentException("Inspection requires a readable, seekable stream.", nameof(s));
            stream = s;
            Context = InspectionContext.For(s);
        }

        /// <summary>
        /// Override the ToString method to return empty.
        /// </summary>
        /// <returns>Empty string value</returns>
        public override string ToString()
        {
            return "";
        }

        /// <summary>
        /// Read  bits value from byte
        /// </summary>
        /// <param name="b">The byte.</param>
        /// <param name="index">The bit index to read</param>
        /// <param name="length">The bit length to read</param>
        /// <returns>bits value</returns>
        public byte GetBits(byte b, int index, int length)
        {
            int Bit = 0;
            int tempBit = 0;
            if ((index >= 8) || (length > 8))
            {
                throw new Exception("The range for index or length should be 0~7.");
            }

            for (int i = length - 1; i >= 0; i--)
            {
                tempBit = ((b & (1 << (index + i))) > 0) ? 1 : 0;
                Bit = (Bit << 1) | tempBit;
            }
            return (byte)Bit;
        }

        /// <summary>
        /// Read bits value from byte
        /// </summary>
        /// <param name="b">The short value used to get bit.</param>
        /// <param name="index">The bit index to read</param>
        /// <param name="length">The bit length to read</param>
        /// <returns>bits value</returns>
        public ushort GetBits(short b, int index, int length)
        {
            byte[] retBytes = new byte[16];
            if ((index >= 16) || (length > 16))
            {
                throw new Exception("The range for index or length should be 0~15.");
            }

            for (int i = 0; i < length; i++)
            {
                int tempBit = ((b & (1 << (index + i))) > 0) ? 1 : 0;
                retBytes[i] = (byte)tempBit;
            }
            return checked((ushort)ConvertFromBytes(retBytes, 16));
        }

        /// <summary>
        /// Read bits value from byte
        /// </summary>
        /// <param name="b">The int value used to get bit.</param>
        /// <param name="index">The bit index to read</param>
        /// <param name="length">The bit length to read</param>
        /// <returns>bits value</returns>
        public uint GetBits(int b, int index, int length)
        {
            byte[] retBytes = new byte[32];
            if ((index >= 32) || (length > 32))
            {
                throw new Exception("The range for index or length should be 0~32.");
            }

            for (int i = 0; i < length; i++)
            {
                int tempBit = ((b & (1 << (index + i))) > 0) ? 1 : 0;
                retBytes[i] = (byte)tempBit;
            }
            return checked((uint)ConvertFromBytes(retBytes, 32));
        }

        /// <summary>
        /// Read bits value from byte
        /// </summary>
        /// <param name="b">The long value used to get bit.</param>
        /// <param name="index">The bit index to read</param>
        /// <param name="length">The bit length to read</param>
        /// <returns>bits value</returns>
        public ulong GetBits(long b, int index, int length)
        {
            byte[] retBytes = new byte[64];
            if ((index >= 64) || (length > 64))
            {
                throw new Exception("The range for index or length should be 0~63.");
            }

            for (int i = 0; i < length; i++)
            {
                int tempBit = ((b & (1 << (index + i))) > 0) ? 1 : 0;
                retBytes[i] = (byte)tempBit;
            }

            return checked((ulong)ConvertFromBytes(retBytes, 64));
        }

        /// <summary>
        /// Returns a value built from the specified number of bytes from the given buffer
        /// </summary>
        /// <param name="buffer">Specify the data in byte array format</param>
        /// <param name="bytesToConvert">Specify the number of bytes to use</param>
        /// <returns>Return the value built from the given bytes</returns>
        private static ulong ConvertFromBytes(byte[] buffer, int bytesToConvert)
        {
            ulong ret = 0;
            int bitCount = 0;
            for (int i = 0; i < bytesToConvert; i++)
            {
                ret |= (ulong)buffer[i] << bitCount;

                bitCount += 1;
            }

            return ret;
        }

        /// <summary>
        /// Read an Int16 value from stream
        /// </summary>
        /// <returns>An Int16 value</returns>
        protected Int16 ReadINT16()
        {
            int value;
            int b1, b2;
            b1 = ReadByte();
            b2 = ReadByte();

            value = (b2 << 8) | b1;

            return (Int16)value;
        }

        /// <summary>
        /// Read an Int32 value from stream
        /// </summary>
        /// <returns>An Int32 value</returns>
        protected Int32 ReadINT32()
        {
            long value;
            int b1, b2, b3, b4;
            b1 = ReadByte();
            b2 = ReadByte();
            b3 = ReadByte();
            b4 = ReadByte();

            value = (b4 << 24) | (b3 << 16) | (b2 << 8) | b1;

            return (Int32)value;
        }

        /// <summary>
        /// Read an long value from stream
        /// </summary>
        /// <returns>An long value</returns>
        public long ReadINT64()
        {
            long low = (uint)this.ReadINT32();
            long high = this.ReadINT32();

            // 0x100000000 is 2 raised to the 32th power plus 1
            return (long)((high << 32) | low);
        }

        /// <summary>
        /// Read an Boolean value from stream
        /// </summary>
        /// <returns>An Boolean value</returns>
        protected Boolean ReadBoolean()
        {
            return ReadByte() != 0x00;
        }

        /// <summary>
        /// Read a byte value from stream
        /// </summary>
        /// <returns>A byte</returns>
        protected byte ReadByte()
        {
            int value = stream.ReadByte();
            if (value == -1)
            {
                throw new EndOfStreamException("Incomplete inspector payload.");
            }
            return (byte)value;
        }

        /// <summary>
        /// Read a GUID value from stream
        /// </summary>
        /// <returns>A GUID value</returns>
        protected Guid ReadGuid()
        {
            Guid guid = new Guid(ReadBytes(16));
            return guid;
        }

        /// <summary>
        /// Read an ushort value from stream
        /// </summary>
        /// <returns>An ushort value</returns>
        protected ushort ReadUshort()
        {
            int value;
            int b1, b2;
            b1 = ReadByte();
            b2 = ReadByte();

            value = (b2 << 8) | b1;

            return (ushort)value;
        }

        /// <summary>
        /// Read an uint value from stream
        /// </summary>
        /// <returns>An uint value</returns>
        protected uint ReadUint()
        {
            long value;
            int b1, b2, b3, b4;
            b1 = ReadByte();
            b2 = ReadByte();
            b3 = ReadByte();
            b4 = ReadByte();

            value = (b4 << 24) | (b3 << 16) | (b2 << 8) | b1;

            return (uint)value;
        }

        /// <summary>
        /// Read an uLong value from stream
        /// </summary>
        /// <returns>An uLong value</returns>
        protected ulong ReadUlong()
        {
            long low = (uint)this.ReadUint();
            long high = (uint)this.ReadUint();

            return (ulong)(high << 32 | low);
        }

        /// <summary>
        /// Read an three bytes value as uint from stream
        /// </summary>
        /// <returns>An int value</returns>
        protected int Read3Bytes()
        {
            long value;
            int b1, b2, b3;
            b1 = ReadByte();
            b2 = ReadByte();
            b3 = ReadByte();

            value = (b3 << 16) | (b2 << 8) | b1;

            return (int)value;
        }

        /// <summary>
        /// Read an five bytes value as ulong from stream
        /// </summary>
        /// <returns>An long value</returns>
        protected long Read5Bytes()
        {
            long value;
            int b1, b2, b3, b4, b5;
            b1 = ReadByte();
            b2 = ReadByte();
            b3 = ReadByte();
            b4 = ReadByte();
            b5 = ReadByte();

            value = (b5 << 32) | (b4 << 24) | (b3 << 16) | (b2 << 8) | b1;
            return (long)value;
        }

        /// <summary>
        /// Read an six bytes value as ulong from stream
        /// </summary>
        /// <returns>An Long value</returns>
        protected long Read6Bytes()
        {
            long low = (int)this.Read3Bytes();
            long high = (int)this.Read3Bytes();

            return high << 24 | low;
        }

        /// <summary>
        /// Read an seven bytes value as ulong from stream
        /// </summary>
        /// <returns>An Long value</returns>
        protected long Read7Bytes()
        {
            long b1 = (int)this.Read3Bytes();
            long b2 = (int)this.ReadINT32();

            return b2 << 32 | b1;
        }

        /// <summary>
        /// Read string value from stream according to string terminator and Encoding method
        /// </summary>
        /// <param name="encoding">The character Encoding</param>
        /// <param name="terminator">The string terminator</param>
        /// <param name="length">The string length.</param>
        /// <param name="reducedUnicode">True means reduced Unicode character string. The terminating null character is one zero byte.</param>
        /// <returns>A string value</returns>
        protected string ReadString(Encoding encoding, string terminator = "\0", int stringlength = 0, bool reducedUnicode = false)
        {
            string result = null;
            StringBuilder value = new StringBuilder();
            if (stringlength == 0)
            {
                int length = terminator.Length;
                bool terminated = false;
                // Read Null-terminated reduced Unicode character string. The terminating null character is one zero byte.
                if ((encoding == Encoding.Unicode) && (reducedUnicode))
                {
                    while (!terminated)
                    {
                        byte[] tempbytes = new byte[2];
                        tempbytes[0] = ReadByte();
                        if (Encoding.ASCII.GetChars(tempbytes, 0, 1)[0].ToString() == "\0")
                        {
                            terminated = true;
                            break;
                        }
                        tempbytes[1] = ReadByte();
                        char[] chars = Encoding.Unicode.GetChars(tempbytes, 0, 2);
                        value.Append(chars);
                    }
                    result = value.ToString();
                }
                else
                {
                    while (!terminated)
                    {
                        value.Append(ReadChar(encoding));
                        if (value.Length < length)
                        {
                            continue;
                        }
                        int i;
                        for (i = length - 1; i >= 0; i--)
                        {
                            if (terminator[i] != value[value.Length - length + i])
                            {
                                break;
                            }
                        }
                        terminated = i < 0;
                    }
                    result = value.Remove(value.Length - length, length).ToString();
                }
            }
            else
            {
                int size = stringlength;
                while (size != 0)
                {
                    value.Append(ReadChar(encoding));
                    size--;
                }
                result = value.ToString();
            }
            return result;
        }

        /// <summary>
        /// Read bytes from stream
        /// </summary>
        /// <param name="length">The byte length to read</param>
        /// <returns>Bytes value</returns>
        protected byte[] ReadBytes(int length)
        {
            if (length < 0 || length > stream.Length - stream.Position)
                throw new InvalidDataException("Byte length exceeds the remaining inspector payload.");
            byte[] bytes = new byte[length];
            stream.ReadExactly(bytes);
            return bytes;
        }

        /// <summary>
        /// Read character from stream
        /// </summary>
        /// <param name="encoding">The text encoding</param>
        /// <returns>A char value</returns>
        protected char ReadChar(Encoding encoding)
        {
            int length = encoding.GetMaxByteCount(1);
            byte[] bytes = new byte[length];
            int count = stream.Read(bytes, 0, length);
            if (count == -1)
            {
                throw new EndOfStreamException("Incomplete inspector payload.");
            }
            char[] chars = encoding.GetChars(bytes, 0, count);

            length = encoding.GetByteCount(chars, 0, 1);
            if (length < count)
            {
                stream.Seek(length - count, SeekOrigin.Current);
            }
            return chars[0];
        }

        /// <summary>
        /// Get the current byte of stream
        /// </summary>
        /// <returns>the current byte of stream</returns>
        protected byte CurrentByte()
        {
            byte current = this.ReadByte();
            stream.Position -= 1;

            return current;
        }

        /// <summary>
        /// Get the next four bytes of stream
        /// </summary>
        /// <returns>the next four bytes of stream</returns>
        protected byte[] NextFourBytes()
        {
            byte[] fourBytes = this.ReadBytes(4);
            stream.Position -= 4;

            return fourBytes;
        }

        #region Helper for FSHHTPB
        /// <summary>
        /// specify whether stream contains Stream Object Header
        /// </summary>
        /// <param name="headerType">a ushort indicate the type of the stream object header</param>
        /// <returns>bool value indicates whether the stream contains Stream object header in current postion</returns>
        public bool ContainsStreamObjectHeader(ushort headerType)
        {
            byte lsb = ReadByte();
            stream.Position -= 1;

            // StreamObjectStart16BitHeader
            if ((lsb & 0x03) == 0x0)
            {
                long header16BitValue = GetBigEnidan(2);
                return (header16BitValue >> 3 & 0x3F) == headerType;
            }

            // StreamObjectStart32BitHeader
            if ((lsb & 0x03) == 0x2)
            {
                long header32BitValue = GetBigEnidan(3);
                return (header32BitValue >> 3 & 0x3FFF) == headerType;
            }
            return false;
        }

        /// <summary>
        /// specify whether stream contains Stream Object Start 16Bit Header
        /// </summary>
        /// <param name="headerType">a ushort indicate the type of the 16 bit stream object header</param>
        /// <returns>bool value indicates whether the stream contains a 16bit Stream object header in current postion</returns>
        public bool ContainsStreamObjectStart16BitHeader(ushort headerType)
        {
            byte lsb = ReadByte();
            stream.Position -= 1;
            if ((lsb & 0x03) == 0x0)
            {
                long header16BitValue = GetBigEnidan(2);
                return (header16BitValue >> 3 & 0x3F) == headerType;
            }
            return false;
        }

        /// <summary>
        /// specify whether stream contains Stream Object Start 32Bit Header
        /// </summary>
        /// <param name="headerType">a ushort indicate the type of the 32 bit stream object header</param>
        /// <returns>bool value indicates whether the stream contains a 32bit Stream object header in current postion</returns>
        public bool ContainsStreamObjectStart32BitHeader(ushort headerType)
        {
            byte lsb = ReadByte();
            stream.Position -= 1;
            // StreamObjectStart32BitHeader
            if ((lsb & 0x03) == 0x2)
            {
                long header32BitValue = GetBigEnidan(3);
                return (header32BitValue >> 3 & 0x3FFF) == headerType;
            }

            return false;
        }

        /// <summary>
        /// read bytes from stream to long in big enidan.
        /// </summary>
        /// <param name="byteNumber">byte number need to read</param>
        /// <returns>the big endian int value</returns>
        public long GetBigEnidan(int byteNumber)
        {
            long result = 0x0;

            for (int i = 0; i < byteNumber; i++)
            {
                long byteOfi = ReadByte();
                result = result | (byteOfi << 8 * i);
            }
            stream.Position -= byteNumber;
            return result;
        }

        /// <summary>
        /// Get the length of the ExtenedGUID
        /// </summary>
        /// <param name="byteOffset">the offset from the current position of the stream</param>
        /// <returns>the size of the ExtendedGUID in bytes</returns>
        public int GetExtendedGUIDBytesLen(int byteOffset)
        {
            stream.Position = stream.Position + byteOffset;
            byte lsb = ReadByte();
            stream.Position -= (byteOffset + 1);
            if (lsb == 0x0)
                return 1;

            if (lsb == 0x80)
                return 21;

            if ((lsb & 0x07) == 0x4)
                return 17;

            if ((lsb & 0x3F) == 0x20)
                return 18;

            if ((lsb & 0x7F) == 0x40)
                return 19;

            return 0;
        }

        /// <summary>
        /// Get the length of the SerialNumber
        /// </summary>
        /// <param name="byteOffset">the offset from the current position of the stream</param>
        /// <returns>the size of the SerialNumber in bytes</returns>
        public int GetSerialNumberBytesLen(int byteOffset)
        {
            stream.Position = stream.Position + byteOffset;
            byte lsb = ReadByte();
            stream.Position -= (byteOffset + 1);
            if (lsb == 0x0)
                return 1;

            if (lsb == 0x80)
                return 25;

            return 0;
        }

        /// <summary>
        /// Read CompactUnsigned64bitInteger from stream
        /// </summary>
        /// <param name="byteOffset">the offset from the current position of the stream</param>
        /// <returns></returns>
        public long ReadCompactUnsigned64bitIntegerValue(int byteOffset)
        {
            stream.Position = stream.Position + byteOffset;
            byte lsb = ReadByte();
            stream.Position -= (byteOffset + 1);
            if (lsb == 0x0)
                return lsb;

            if (lsb == 0x80)
                return GetBigEnidan(8);

            if ((lsb & 0x01) == 0x1)
                return (lsb >> 1 & 0x7F);

            if ((lsb & 0x03) == 0x2)
            {
                long rawValue = GetBigEnidan(2);
                return ((rawValue >> 2) & 0x3FFF);
            }

            if ((lsb & 0x07) == 0x4)
            {
                long rawValue = GetBigEnidan(3);
                return (rawValue >> 3 & 0x1FFFFF);
            }

            if ((lsb & 0x0F) == 0x8)
            {
                long rawValue = GetBigEnidan(4);
                return (rawValue >> 4 & 0xFFFFFFF);
            }

            if ((lsb & 0x1F) == 0x10)
            {
                long rawValue = GetBigEnidan(5);
                return (rawValue >> 5 & 0x7FFFFFFFF);
            }

            if ((lsb & 0x3F) == 0x20)
            {
                long rawValue = GetBigEnidan(6);
                return (rawValue >> 6 & 0x3FFFFFFFFFF);
            }

            if ((lsb & 0x7F) == 0x40)
            {
                long rawValue = GetBigEnidan(7);
                return (rawValue >> 7 & 0x1FFFFFFFFFFFF);
            }

            return 0;
        }

        /// <summary>
        /// Get DataElementPackage Type
        /// </summary>
        /// <returns>DataElementPackage type</returns>
        public long PreReadDataElementPackageType()
        {
            if (!ContainsStreamObjectHeader(0x01)) return -1;
            long position = stream.Position;
            try
            {
                new StreamObjectHeader().TryParse(stream);
                new ExtendedGUID().TryParse(stream);
                new SerialNumber().TryParse(stream);
                var type = new CompactUnsigned64bitInteger().TryParse(stream);
                return checked((long)type.GetUint(type));
            }
            finally
            {
                stream.Position = position;
            }
        }

        internal bool ContainsStreamObjectEndHeader(ushort headerType)
        {
            long position = stream.Position;
            try
            {
                int first = ReadByte();
                int discriminator = first & 3;
                if (discriminator is not (1 or 3)) return false;
                int value = discriminator == 3 ? first | (ReadByte() << 8) : first;
                return value >> 2 == headerType;
            }
            finally
            {
                stream.Position = position;
            }
        }
        #endregion
    }
}
