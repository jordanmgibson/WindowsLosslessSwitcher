using System.Buffers.Binary;
using System.IO;

namespace WindowsLosslessSwitcher.Services;

public sealed class BinaryPlistReader
{
    public Dictionary<string, object?> ReadRootDictionary(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return ReadRootDictionary(bytes);
    }

    public Dictionary<string, object?> ReadRootDictionary(byte[] bytes)
    {
        var parser = new Parser(bytes);
        return parser.ReadRootDictionary();
    }

    private sealed class Parser
    {
        private readonly byte[] _bytes;
        private readonly int _offsetSize;
        private readonly int _objectRefSize;
        private readonly long _numObjects;
        private readonly long _topObject;
        private readonly long _offsetTableOffset;

        public Parser(byte[] bytes)
        {
            _bytes = bytes;
            if (_bytes.Length < 40 || !_bytes.AsSpan(0, 8).SequenceEqual("bplist00"u8))
            {
                throw new InvalidDataException("Unsupported plist format.");
            }

            var trailer = _bytes.AsSpan(_bytes.Length - 32, 32);
            _offsetSize = trailer[6];
            _objectRefSize = trailer[7];
            _numObjects = ReadSizedUnsignedInteger(trailer[8..16]);
            _topObject = ReadSizedUnsignedInteger(trailer[16..24]);
            _offsetTableOffset = ReadSizedUnsignedInteger(trailer[24..32]);
        }

        public Dictionary<string, object?> ReadRootDictionary()
        {
            var root = ReadObject((int)_topObject);
            return root as Dictionary<string, object?> ?? throw new InvalidDataException("Root object is not a dictionary.");
        }

        private object? ReadObject(int objectIndex)
        {
            var offset = (int)ReadOffset(objectIndex);
            var marker = _bytes[offset];
            var objectType = marker >> 4;
            var objectInfo = marker & 0x0F;

            return objectType switch
            {
                0x0 => objectInfo switch
                {
                    0x0 => null,
                    0x8 => false,
                    0x9 => true,
                    _ => null,
                },
                0x1 => ReadInteger(offset, objectInfo),
                0x2 => ReadReal(offset, objectInfo),
                0x4 => ReadData(offset, objectInfo),
                0x5 => ReadAsciiString(offset, objectInfo),
                0x6 => ReadUtf16String(offset, objectInfo),
                0xA => ReadArray(offset, objectInfo),
                0xD => ReadDictionary(offset, objectInfo),
                _ => null,
            };
        }

        private long ReadInteger(int offset, int objectInfo)
        {
            var byteCount = 1 << objectInfo;
            return ReadSizedUnsignedInteger(_bytes.AsSpan(offset + 1, byteCount));
        }

        private double ReadReal(int offset, int objectInfo)
        {
            var byteCount = 1 << objectInfo;
            var data = _bytes.AsSpan(offset + 1, byteCount);
            return byteCount switch
            {
                4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data)),
                8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data)),
                _ => 0,
            };
        }

        private byte[] ReadData(int offset, int objectInfo)
        {
            var (count, headerLength) = ReadCount(offset, objectInfo);
            var start = offset + headerLength;
            return _bytes.AsSpan(start, count).ToArray();
        }

        private string ReadAsciiString(int offset, int objectInfo)
        {
            var (count, headerLength) = ReadCount(offset, objectInfo);
            return System.Text.Encoding.ASCII.GetString(_bytes, offset + headerLength, count);
        }

        private string ReadUtf16String(int offset, int objectInfo)
        {
            var (count, headerLength) = ReadCount(offset, objectInfo);
            return System.Text.Encoding.BigEndianUnicode.GetString(_bytes, offset + headerLength, count * 2);
        }

        private List<object?> ReadArray(int offset, int objectInfo)
        {
            var (count, headerLength) = ReadCount(offset, objectInfo);
            var list = new List<object?>(count);
            var refsOffset = offset + headerLength;
            for (var i = 0; i < count; i++)
            {
                var objectRef = ReadObjectReference(refsOffset + (i * _objectRefSize));
                list.Add(ReadObject(objectRef));
            }

            return list;
        }

        private Dictionary<string, object?> ReadDictionary(int offset, int objectInfo)
        {
            var (count, headerLength) = ReadCount(offset, objectInfo);
            var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            var keyRefsOffset = offset + headerLength;
            var valueRefsOffset = keyRefsOffset + (count * _objectRefSize);

            for (var i = 0; i < count; i++)
            {
                var keyRef = ReadObjectReference(keyRefsOffset + (i * _objectRefSize));
                var valueRef = ReadObjectReference(valueRefsOffset + (i * _objectRefSize));
                var key = ReadObject(keyRef) as string;
                if (!string.IsNullOrEmpty(key))
                {
                    dictionary[key] = ReadObject(valueRef);
                }
            }

            return dictionary;
        }

        private (int Count, int HeaderLength) ReadCount(int offset, int objectInfo)
        {
            if (objectInfo < 0x0F)
            {
                return (objectInfo, 1);
            }

            var lengthMarkerOffset = offset + 1;
            var marker = _bytes[lengthMarkerOffset];
            var markerType = marker >> 4;
            var markerInfo = marker & 0x0F;
            if (markerType != 0x1)
            {
                throw new InvalidDataException("Unexpected length marker in binary plist.");
            }

            var byteCount = 1 << markerInfo;
            var count = (int)ReadSizedUnsignedInteger(_bytes.AsSpan(lengthMarkerOffset + 1, byteCount));
            return (count, 2 + byteCount);
        }

        private int ReadObjectReference(int offset) =>
            (int)ReadSizedUnsignedInteger(_bytes.AsSpan(offset, _objectRefSize));

        private long ReadOffset(int objectIndex)
        {
            var offsetPosition = (int)_offsetTableOffset + (objectIndex * _offsetSize);
            return ReadSizedUnsignedInteger(_bytes.AsSpan(offsetPosition, _offsetSize));
        }

        private static long ReadSizedUnsignedInteger(ReadOnlySpan<byte> bytes)
        {
            long result = 0;
            foreach (var value in bytes)
            {
                result = (result << 8) | value;
            }

            return result;
        }
    }
}
