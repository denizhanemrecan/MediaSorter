namespace MediaSorter.Core.Recovery;

public sealed record HeaderConfig(bool Cabac, int FrameNumBits, int PocType, int PocLsbBits, bool DeblockingControl, uint PpsId)
{
    public override string ToString() =>
        $"{(Cabac ? "CABAC" : "CAVLC")}, frame_num {FrameNumBits}b, POC type {PocType}{(PocType == 0 ? $" ({PocLsbBits}b)" : "")}, deblock ctrl {(DeblockingControl ? 1 : 0)}, pps {PpsId}";
}

public enum ProfileConfig
{
    HighTransform8x8,
    High,
    Main,
    Baseline
}

public sealed record StreamParameters(HeaderConfig Header, ProfileConfig Profile, int Qp, int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}, {Profile}, QP {Qp}, {Header}";
}

internal static class ParameterSets
{
    private const int MaxRefFrames = 4;

    public static byte[] BuildSps(HeaderConfig h, ProfileConfig profile, int width, int height)
    {
        int profileIdc = profile switch
        {
            ProfileConfig.HighTransform8x8 or ProfileConfig.High => 100,
            ProfileConfig.Main => 77,
            _ => 66
        };
        int widthMbs = (width + 15) / 16;
        int heightMbs = (height + 15) / 16;
        int cropRight = (widthMbs * 16 - width) / 2;
        int cropBottom = (heightMbs * 16 - height) / 2;

        var w = new BitWriter()
            .U(8, profileIdc)
            .U(8, 0)
            .U(8, 51)
            .Ue(0);
        if (profileIdc >= 100)
            w.Ue(1).Ue(0).Ue(0).Flag(false).Flag(false);
        w.Ue((uint)(h.FrameNumBits - 4)).Ue((uint)h.PocType);
        if (h.PocType == 0)
            w.Ue((uint)(h.PocLsbBits - 4));
        w.Ue(MaxRefFrames)
            .Flag(false)
            .Ue((uint)(widthMbs - 1))
            .Ue((uint)(heightMbs - 1))
            .Flag(true)
            .Flag(true);
        if (cropRight != 0 || cropBottom != 0)
            w.Flag(true).Ue(0).Ue((uint)cropRight).Ue(0).Ue((uint)cropBottom);
        else
            w.Flag(false);
        w.Flag(false);
        return w.ToNal(0x67);
    }

    public static byte[] BuildPps(HeaderConfig h, ProfileConfig profile, int qp)
    {
        var w = new BitWriter()
            .Ue(h.PpsId)
            .Ue(0)
            .Flag(h.Cabac)
            .Flag(false)
            .Ue(0)
            .Ue(0)
            .Ue(0)
            .Flag(false)
            .U(2, 0)
            .Se(qp - 26)
            .Se(0)
            .Se(0)
            .Flag(h.DeblockingControl)
            .Flag(false)
            .Flag(false);
        if (profile is ProfileConfig.HighTransform8x8 or ProfileConfig.High)
            w.Flag(profile == ProfileConfig.HighTransform8x8).Flag(false).Se(0);
        return w.ToNal(0x68);
    }

    public static byte[] AnnexB(IEnumerable<ReadOnlyMemory<byte>> nals)
    {
        using var ms = new MemoryStream();
        foreach (var nal in nals)
        {
            ms.Write([0, 0, 0, 1]);
            ms.Write(nal.Span);
        }
        return ms.ToArray();
    }
}
