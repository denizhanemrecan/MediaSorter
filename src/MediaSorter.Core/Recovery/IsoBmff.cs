using System.Buffers.Binary;
using System.Text;

namespace MediaSorter.Core.Recovery;

public sealed record IsoBox(string Type, long Offset, int HeaderSize, long Size, long DeclaredSize)
{
    public bool IsTruncated => DeclaredSize > Size;
    public long PayloadOffset => Offset + HeaderSize;
    public long End => Offset + Size;
}

public static class IsoBmff
{
    public static List<IsoBox> ReadTopLevel(Stream stream)
    {
        var boxes = new List<IsoBox>();
        long length = stream.Length;
        long p = 0;
        Span<byte> hdr = stackalloc byte[16];
        while (p + 8 <= length)
        {
            stream.Position = p;
            int got = stream.ReadAtLeast(hdr, 16, throwOnEndOfStream: false);
            if (got < 8)
                break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            var type = Encoding.ASCII.GetString(hdr[4..8]);
            int headerSize = 8;
            if (size == 1)
            {
                if (got < 16) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = length - p;
            }

            if (size < headerSize || !IsPrintable(type))
                break;

            long available = Math.Min(size, length - p);
            boxes.Add(new IsoBox(type, p, headerSize, available, size));
            p += size;
        }
        return boxes;
    }

    public static List<IsoBox> ReadTopLevel(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        return ReadTopLevel(ms);
    }

    private static bool IsPrintable(string fourcc) => fourcc.All(c => c is >= ' ' and <= '~' or '©');
}
