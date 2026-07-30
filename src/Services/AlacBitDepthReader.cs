using System.IO;
using System.Text;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Reads the true bit depth of an ALAC stream from its magic cookie. TagLib reports the MP4
/// sample entry's legacy <c>samplesize</c> field (frequently 16 even for 24-bit Hi-Res ALAC) and
/// exposes nothing for AAC, so it cannot distinguish the two — this walks the atom tree
/// <c>moov &gt; trak &gt; mdia &gt; minf &gt; stbl &gt; stsd &gt; alac</c> and reads the depth byte
/// from the ALAC magic cookie. Returns null for non-ALAC containers (e.g. AAC), which is how
/// callers tell lossless ALAC apart from lossy AAC.
/// </summary>
internal static class AlacBitDepthReader
{
    /// <summary>
    /// Opens <paramref name="filePath"/> (sharing write/delete so it works while Apple Music holds
    /// the file open) and returns the ALAC bit depth, or null when the file is not ALAC.
    /// </summary>
    public static int? TryReadBitDepth(string filePath)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return TryReadBitDepth(stream);
    }

    public static int? TryReadBitDepth(Stream stream)
    {
        if (stream is null || !stream.CanSeek)
        {
            return null;
        }

        return FindAlacBitDepth(stream, 0, stream.Length);
    }

    private static int? FindAlacBitDepth(Stream stream, long start, long end)
    {
        var position = start;
        while (position + 8 <= end)
        {
            if (!TryReadBoxHeader(stream, position, end, out var contentStart, out var boxEnd, out var type))
            {
                break;
            }

            switch (type)
            {
                case "moov":
                case "trak":
                case "mdia":
                case "minf":
                case "stbl":
                {
                    var result = FindAlacBitDepth(stream, contentStart, boxEnd);
                    if (result is not null)
                    {
                        return result;
                    }

                    break;
                }
                case "stsd":
                {
                    // stsd is a full box: 1 byte version + 3 bytes flags + 4 bytes entry count
                    // precede the sample entries.
                    var result = FindAlacBitDepth(stream, contentStart + 8, boxEnd);
                    if (result is not null)
                    {
                        return result;
                    }

                    break;
                }
                case "alac":
                {
                    var result = ReadAlacBox(stream, contentStart, boxEnd);
                    if (result is not null)
                    {
                        return result;
                    }

                    break;
                }
            }

            position = boxEnd;
        }

        return null;
    }

    private static int? ReadAlacBox(Stream stream, long contentStart, long boxEnd)
    {
        // The 'alac' four-cc appears twice: first as the sample entry (an AudioSampleEntry whose
        // 28-byte header precedes a nested 'alac' config box), then as that config box (a full box
        // whose 4-byte version/flags precede the 24-byte ALAC magic cookie). Bit depth is byte 5 of
        // the cookie (after frameLength (4 bytes) + compatibleVersion (1 byte)).

        // Sample-entry case: descend past the 28-byte AudioSampleEntry header to the nested box.
        var nested = FindAlacBitDepth(stream, contentStart + 28, boxEnd);
        if (nested is not null)
        {
            return nested;
        }

        // Config-box case: read the magic cookie directly.
        var bitDepthPosition = contentStart + 4 + 5;
        if (bitDepthPosition < boxEnd)
        {
            stream.Position = bitDepthPosition;
            var value = stream.ReadByte();
            if (value is 16 or 20 or 24 or 32)
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryReadBoxHeader(
        Stream stream,
        long position,
        long limit,
        out long contentStart,
        out long boxEnd,
        out string type)
    {
        contentStart = 0;
        boxEnd = 0;
        type = string.Empty;

        if (position + 8 > limit)
        {
            return false;
        }

        stream.Position = position;
        var header = new byte[8];
        if (!ReadExact(stream, header, 8))
        {
            return false;
        }

        var size = (long)ReadUInt32BigEndian(header, 0);
        type = Encoding.ASCII.GetString(header, 4, 4);
        contentStart = position + 8;

        if (size == 1)
        {
            var largeSize = new byte[8];
            if (!ReadExact(stream, largeSize, 8))
            {
                return false;
            }

            size = (long)ReadUInt64BigEndian(largeSize, 0);
            contentStart = position + 16;
        }
        else if (size == 0)
        {
            // Box runs to the end of its parent.
            boxEnd = limit;
            return contentStart <= boxEnd;
        }

        boxEnd = position + size;
        return boxEnd >= contentStart && boxEnd <= limit;
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static uint ReadUInt32BigEndian(byte[] buffer, int offset) =>
        (uint)((buffer[offset] << 24)
            | (buffer[offset + 1] << 16)
            | (buffer[offset + 2] << 8)
            | buffer[offset + 3]);

    private static ulong ReadUInt64BigEndian(byte[] buffer, int offset)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = (value << 8) | buffer[offset + i];
        }

        return value;
    }
}
