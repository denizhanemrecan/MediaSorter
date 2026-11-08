namespace MediaSorter.Core.Recovery;

/// <summary>
/// Infers the SPS/PPS fields that affect slice header syntax by parsing the first IDR and the
/// slices that follow it under every plausible configuration and keeping the ones that stay
/// self-consistent (frame_num continuity, legal syntax element ranges, CABAC alignment bits).
/// </summary>
internal static class SliceHeaderProbe
{
    private const int HeaderBytes = 96;

    private readonly record struct Slice(uint FirstMb, uint SliceType, uint PpsId, uint FrameNum, bool IsIdr);

    public static List<HeaderConfig> Probe(byte[] data, IReadOnlyList<NalUnit> slices, int maxCandidates)
    {
        var rbsps = slices
            .Select(n => (Header: data[n.Offset], Rbsp: Rbsp.FromNal(data.AsSpan(n.Offset, n.Length), 1, HeaderBytes)))
            .ToList();

        var passing = new List<(HeaderConfig Config, int Score)>();
        foreach (var candidate in Candidates())
        {
            int score = Evaluate(candidate, rbsps, out uint ppsId);
            if (score >= 0)
                passing.Add((candidate with { PpsId = ppsId }, score));
        }

        return passing
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Config.PocType == 2 ? 0 : 1)
            .ThenBy(p => p.Config.DeblockingControl ? 1 : 0)
            .ThenBy(p => p.Config.FrameNumBits)
            .ThenBy(p => p.Config.PocLsbBits)
            .Take(maxCandidates)
            .Select(p => p.Config)
            .ToList();
    }

    private static IEnumerable<HeaderConfig> Candidates()
    {
        foreach (bool cabac in new[] { true, false })
        foreach (bool deblock in new[] { false, true })
        for (int fn = 4; fn <= 16; fn++)
        {
            yield return new HeaderConfig(cabac, fn, 2, 0, deblock, 0);
            for (int poc = 4; poc <= 16; poc++)
                yield return new HeaderConfig(cabac, fn, 0, poc, deblock, 0);
        }
    }

    /// <returns>-1 if inconsistent, otherwise a confidence score (more verified constraints = higher).</returns>
    private static int Evaluate(HeaderConfig cfg, List<(byte Header, byte[] Rbsp)> slices, out uint resolvedPpsId)
    {
        resolvedPpsId = 0;
        uint frameNumMask = (1u << cfg.FrameNumBits) - 1;
        uint? ppsId = null;
        uint prevRefFrameNum = 0;
        uint pictureFrameNum = 0;
        int increments = 0;
        int score = 0;
        bool sawIdr = false;

        foreach (var (header, rbsp) in slices)
        {
            int refIdc = header >> 5;
            bool isIdr = NalScanner.NalType(header) == 5;
            if (sawIdr && isIdr && increments > 0 && (rbsp.Length == 0 || (rbsp[0] & 0x80) != 0))
                break;

            Slice s;
            int alignedBits;
            try
            {
                if (!TryParse(cfg, rbsp, refIdc, isIdr, out s, out alignedBits))
                    return -1;
            }
            catch (EndOfStreamException)
            {
                return -1;
            }
            catch (InvalidDataException)
            {
                return -1;
            }

            ppsId ??= s.PpsId;
            if (s.PpsId != ppsId)
                return -1;

            if (isIdr)
            {
                if (s.FrameNum != 0 || s.SliceType % 5 != 2)
                    return -1;
                if (!sawIdr && s.FirstMb != 0)
                    return -1;
                sawIdr = true;
                prevRefFrameNum = 0;
                pictureFrameNum = 0;
            }
            else
            {
                if (!sawIdr)
                    return -1;
                if (s.FirstMb == 0)
                {
                    // 7.4.3: every picture after a reference picture carries PrevRefFrameNum + 1.
                    if (s.FrameNum != ((prevRefFrameNum + 1) & frameNumMask))
                        return -1;
                    pictureFrameNum = s.FrameNum;
                    increments++;
                    if (refIdc != 0)
                        prevRefFrameNum = s.FrameNum;
                }
                else if (s.FrameNum != pictureFrameNum)
                {
                    return -1;
                }
            }
            score += 1 + alignedBits;
        }

        if (!sawIdr || increments == 0)
            return -1;
        resolvedPpsId = ppsId ?? 0;
        return score;
    }

    private static bool TryParse(HeaderConfig cfg, byte[] rbsp, int refIdc, bool isIdr, out Slice slice, out int alignedBits)
    {
        slice = default;
        alignedBits = 0;
        var r = new BitReader(rbsp);

        uint firstMb = r.Ue();
        uint sliceType = r.Ue();
        if (sliceType > 9)
            return false;
        uint ppsId = r.Ue();
        if (ppsId > 255)
            return false;
        uint frameNum = r.U(cfg.FrameNumBits);
        if (isIdr && r.Ue() > 65535)
            return false;
        if (cfg.PocType == 0)
            r.U(cfg.PocLsbBits);

        uint st = sliceType % 5;
        if (st is 3 or 4)
            return false;
        bool isB = st == 1, isP = st == 0;

        if (isB)
            r.Flag();
        if (isP || isB)
        {
            if (r.Flag())
            {
                if (r.Ue() > 31) return false;
                if (isB && r.Ue() > 31) return false;
            }
        }

        if (st != 2)
        {
            if (!ParseRefListModification(r)) return false;
            if (isB && !ParseRefListModification(r)) return false;
        }

        if (refIdc != 0)
        {
            if (isIdr)
            {
                r.Flag();
                r.Flag();
            }
            else if (r.Flag() && !ParseMmco(r))
            {
                return false;
            }
        }

        if (cfg.Cabac && st != 2 && r.Ue() > 2)
            return false;

        int qpDelta = r.Se();
        if (qpDelta is < -51 or > 51)
            return false;

        if (cfg.DeblockingControl)
        {
            uint idc = r.Ue();
            if (idc > 2) return false;
            if (idc != 1)
            {
                int alpha = r.Se(), beta = r.Se();
                if (alpha is < -6 or > 6 || beta is < -6 or > 6) return false;
            }
        }

        if (cfg.Cabac)
        {
            while (!r.IsByteAligned)
            {
                if (!r.Flag())
                    return false;
                alignedBits++;
            }
            // 9.3.1.2: the arithmetic decoder's initial codIOffset must not be 510 or 511.
            if (r.U(9) >= 510)
                return false;
        }

        slice = new Slice(firstMb, sliceType, ppsId, frameNum, isIdr);
        return true;
    }

    private static bool ParseRefListModification(BitReader r)
    {
        if (!r.Flag())
            return true;
        for (int i = 0; i < 33; i++)
        {
            uint idc = r.Ue();
            if (idc == 3) return true;
            if (idc > 3) return false;
            r.Ue();
        }
        return false;
    }

    private static bool ParseMmco(BitReader r)
    {
        for (int i = 0; i < 66; i++)
        {
            uint op = r.Ue();
            switch (op)
            {
                case 0: return true;
                case 1 or 2 or 6: r.Ue(); break;
                case 3: r.Ue(); r.Ue(); break;
                case 4: r.Ue(); break;
                case 5: break;
                default: return false;
            }
        }
        return false;
    }
}
