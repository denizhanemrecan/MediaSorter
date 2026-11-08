namespace MediaSorter.Core.Recovery;

internal sealed class BitReader(byte[] data)
{
    private int _bit;

    public int Position => _bit;
    public bool IsByteAligned => (_bit & 7) == 0;

    public uint U(int n)
    {
        if (_bit + n > data.Length * 8)
            throw new EndOfStreamException();
        uint v = 0;
        for (int i = 0; i < n; i++, _bit++)
            v = (v << 1) | (uint)((data[_bit >> 3] >> (7 - (_bit & 7))) & 1);
        return v;
    }

    public bool Flag() => U(1) == 1;

    public uint Ue()
    {
        int zeros = 0;
        while (U(1) == 0)
            if (++zeros > 31)
                throw new InvalidDataException("Exp-Golomb code too long");
        return zeros == 0 ? 0 : (1u << zeros) - 1 + U(zeros);
    }

    public int Se()
    {
        uint k = Ue();
        return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2);
    }
}

internal sealed class BitWriter
{
    private readonly List<byte> _bits = [];

    public BitWriter U(int n, long value)
    {
        for (int i = n - 1; i >= 0; i--)
            _bits.Add((byte)((value >> i) & 1));
        return this;
    }

    public BitWriter Flag(bool value) => U(1, value ? 1 : 0);

    public BitWriter Ue(uint value)
    {
        ulong v = (ulong)value + 1;
        int len = 64 - System.Numerics.BitOperations.LeadingZeroCount(v);
        U(len - 1, 0);
        return U(len, (long)v);
    }

    public BitWriter Se(int value) => Ue(value > 0 ? (uint)(2 * value - 1) : (uint)(-2 * value));

    public byte[] ToNal(byte header)
    {
        var bits = new List<byte>(_bits) { 1 };
        while (bits.Count % 8 != 0)
            bits.Add(0);

        var rbsp = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
            rbsp[i >> 3] |= (byte)(bits[i] << (7 - (i & 7)));

        var nal = new List<byte>(rbsp.Length + 8) { header };
        int zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                nal.Add(3);
                zeros = 0;
            }
            nal.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return nal.ToArray();
    }
}

internal static class Rbsp
{
    public static byte[] FromNal(ReadOnlySpan<byte> nal, int skipHeaderBytes, int maxBytes)
    {
        var output = new List<byte>(Math.Min(nal.Length, maxBytes));
        int zeros = 0;
        for (int i = skipHeaderBytes; i < nal.Length && output.Count < maxBytes; i++)
        {
            byte b = nal[i];
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }
            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return output.ToArray();
    }
}
