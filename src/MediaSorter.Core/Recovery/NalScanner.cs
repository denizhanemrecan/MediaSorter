using System.Buffers.Binary;

namespace MediaSorter.Core.Recovery;

public readonly record struct NalUnit(int Offset, int Length)
{
    public int End => Offset + Length;
}

public readonly record struct ByteRange(int Start, int End)
{
    public int Length => End - Start;
}

public sealed record NalScan(IReadOnlyList<NalUnit> Nals, IReadOnlyList<ByteRange> Gaps, long VideoBytes);

public enum VideoCodecGuess
{
    None,
    Avc,
    Hevc
}

/// <summary>
/// Walks length-prefixed (AVCC style) NAL units inside a raw mdat payload, resynchronising
/// across interleaved non-video chunks. Without a sample table this is the only way to
/// separate video access units from audio.
/// </summary>
public static class NalScanner
{
    private const int MaxNalSize = 16 * 1024 * 1024;
    private const int StartChain = 4;
    private const int ResyncChain = 3;

    public static NalScan? ScanAvc(byte[] d, int start, int end)
    {
        int p = FindChain(d, start, end, StartChain, AvcNalLength);
        if (p < 0)
            return null;

        var nals = new List<NalUnit>();
        var gaps = new List<ByteRange>();
        if (p > start)
            gaps.Add(new ByteRange(start, p));

        long videoBytes = 0;
        while (p + 5 <= end)
        {
            int len = AvcNalLength(d, p, end);
            if (len > 0)
            {
                nals.Add(new NalUnit(p + 4, len));
                videoBytes += len + 4;
                p += 4 + len;
                continue;
            }
            int next = FindChain(d, p + 1, end, ResyncChain, AvcNalLength);
            if (next < 0)
                break;
            gaps.Add(new ByteRange(p, next));
            p = next;
        }
        return new NalScan(nals, gaps, videoBytes);
    }

    public static VideoCodecGuess Identify(byte[] d, int start, int end)
    {
        if (FindChain(d, start, end, StartChain, AvcNalLength) >= 0)
            return VideoCodecGuess.Avc;
        if (FindChain(d, start, end, StartChain, HevcNalLength) >= 0)
            return VideoCodecGuess.Hevc;
        return VideoCodecGuess.None;
    }

    public static int NalType(byte header) => header & 0x1F;

    public static bool IsAvcHeader(byte h)
    {
        if ((h & 0x80) != 0)
            return false;
        int refIdc = h >> 5;
        return (h & 0x1F) switch
        {
            1 => true,
            5 or 7 or 8 => refIdc != 0,
            6 or 9 or 12 => refIdc == 0,
            _ => false
        };
    }

    private static int AvcNalLength(byte[] d, int p, int end)
    {
        if (p + 5 > end)
            return 0;
        int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p));
        if (len < 2 || len > MaxNalSize || p + 4 + len > end)
            return 0;
        return IsAvcHeader(d[p + 4]) ? len : 0;
    }

    private static int HevcNalLength(byte[] d, int p, int end)
    {
        if (p + 6 > end)
            return 0;
        int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p));
        if (len < 3 || len > MaxNalSize || p + 4 + len > end)
            return 0;
        byte h0 = d[p + 4], h1 = d[p + 5];
        if ((h0 & 0x81) != 0 || h1 != 1)
            return 0;
        int type = h0 >> 1;
        return type is <= 9 or 19 or 20 or 21 or 32 or 33 or 34 or 35 or 39 or 40 ? len : 0;
    }

    private static int FindChain(byte[] d, int from, int end, int need, Func<byte[], int, int, int> lengthAt)
    {
        for (int i = from; i + 5 <= end; i++)
        {
            int p = i, count = 0;
            while (count < need)
            {
                int len = lengthAt(d, p, end);
                if (len == 0)
                    break;
                count++;
                p += 4 + len;
                if (p + 5 > end)
                {
                    count = need;
                    break;
                }
            }
            if (count >= need)
                return i;
        }
        return -1;
    }
}
