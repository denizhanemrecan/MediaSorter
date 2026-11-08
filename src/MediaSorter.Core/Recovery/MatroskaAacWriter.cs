using System.Buffers.Binary;
using System.Text;

namespace MediaSorter.Core.Recovery;

/// <summary>
/// Wraps raw AAC chunks (frame boundaries unknown) as one Matroska block per chunk. libavcodec's
/// AAC decoder consumes one raw_data_block per call and is re-fed the remainder, so every frame
/// inside a chunk is decoded in order without needing the lost sample-size table.
/// </summary>
internal static class MatroskaAacWriter
{
    public static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000];

    public static byte[] Build(byte[] source, IReadOnlyList<ByteRange> chunks, int sampleRate, int channels)
    {
        int srIndex = Array.IndexOf(SampleRates, sampleRate);
        if (srIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var asc = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(asc, (ushort)((2 << 11) | (srIndex << 7) | (channels << 3)));

        using var ms = new MemoryStream();
        Element(ms, [0x1A, 0x45, 0xDF, 0xA3], Concat(
            Element([0x42, 0x82], "matroska"u8.ToArray()),
            UInt([0x42, 0x87], 2),
            UInt([0x42, 0x85], 2)));

        ms.Write([0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        Element(ms, [0x15, 0x49, 0xA9, 0x66], UInt([0x2A, 0xD7, 0xB1], 1_000_000));

        var freq = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(freq, sampleRate);
        var track = Concat(
            UInt([0xD7], 1),
            UInt([0x73, 0xC5], 1),
            UInt([0x83], 2),
            Element([0x86], Encoding.ASCII.GetBytes("A_AAC")),
            Element([0x63, 0xA2], asc),
            Element([0xE1], Concat(Element([0xB5], freq), UInt([0x9F], (ulong)channels))));
        Element(ms, [0x16, 0x54, 0xAE, 0x6B], Element([0xAE], track));

        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var block = new byte[4 + c.Length];
            block[0] = 0x81;
            block[3] = 0x80;
            Buffer.BlockCopy(source, c.Start, block, 4, c.Length);
            Element(ms, [0x1F, 0x43, 0xB6, 0x75], Concat(UInt([0xE7], (ulong)(i * 1000 + 1)), Element([0xA3], block)));
        }
        return ms.ToArray();
    }

    private static void Element(Stream s, byte[] id, byte[] payload)
    {
        s.Write(id);
        s.Write(Size(payload.Length));
        s.Write(payload);
    }

    private static byte[] Element(byte[] id, byte[] payload)
    {
        using var ms = new MemoryStream();
        Element(ms, id, payload);
        return ms.ToArray();
    }

    private static byte[] UInt(byte[] id, ulong value)
    {
        int len = Math.Max(1, (64 - System.Numerics.BitOperations.LeadingZeroCount(value) + 7) / 8);
        var payload = new byte[len];
        for (int i = 0; i < len; i++)
            payload[len - 1 - i] = (byte)(value >> (8 * i));
        return Element(id, payload);
    }

    private static byte[] Size(long n)
    {
        for (int len = 1; len <= 8; len++)
        {
            if (n < (1L << (7 * len)) - 1)
            {
                var b = new byte[len];
                for (int i = 0; i < len; i++)
                    b[len - 1 - i] = (byte)(n >> (8 * i));
                b[0] |= (byte)(0x80 >> (len - 1));
                return b;
            }
        }
        throw new ArgumentOutOfRangeException(nameof(n));
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
