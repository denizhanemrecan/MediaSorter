using System.Globalization;
using System.Text.RegularExpressions;
using MediaSorter.Core.Tools;

namespace MediaSorter.Core.Recovery;

internal readonly record struct DecodeScore(bool Clean, int FirstErrorMb, int ErrorLines)
{
    public long Rank => Clean ? long.MaxValue : FirstErrorMb;
}

/// <summary>Uses ffmpeg's H.264 decoder as a ground-truth check for guessed parameter sets.</summary>
internal sealed partial class DecodeOracle(FfmpegTools ffmpeg)
{
    public async Task<DecodeScore> ScoreAsync(byte[] annexB, int widthMbs, CancellationToken ct)
    {
        var r = await ffmpeg.RunFfmpegAsync(["-v", "warning", "-f", "h264", "-i", "pipe:0", "-f", "null", "-"], ct, annexB).ConfigureAwait(false);
        // A slice that desyncs into an early end_of_slice is only reported as a concealed
        // ("corrupt decoded frame") warning, so warnings count as failures too.
        var lines = r.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains("h264 @", StringComparison.Ordinal) || l.Contains("corrupt", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.Contains("probesize", StringComparison.Ordinal))
            .ToArray();
        if (lines.Length == 0 && r.ExitCode == 0)
            return new DecodeScore(true, int.MaxValue, 0);

        int firstError = 0;
        var m = MbErrorPattern().Match(r.StdErr);
        if (m.Success)
        {
            int x = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int y = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            firstError = y * widthMbs + x;
        }
        return new DecodeScore(false, firstError, lines.Length);
    }

    [GeneratedRegex(@"error while decoding MB (\d+) (\d+)")]
    private static partial Regex MbErrorPattern();
}
