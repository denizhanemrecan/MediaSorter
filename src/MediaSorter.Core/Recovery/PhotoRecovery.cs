using System.Buffers.Binary;
using System.Globalization;
using MediaSorter.Core.Tools;
using MetadataExtractor;
using Directory = System.IO.Directory;

namespace MediaSorter.Core.Recovery;

public sealed record PhotoRecoveryResult(bool Success, string Message, IReadOnlyList<string> Outputs);

/// <summary>
/// Salvages what is left of damaged JPEGs (typically carved by data-recovery tools, where only the
/// first fragment of the file is really theirs):
/// 1. the largest intact embedded preview (EXIF / APPn thumbnails sit at the start of the file),
/// 2. the intact top region of the main image, cut where decoded rows turn into garbage or fill.
/// </summary>
public sealed class PhotoRecovery(FfmpegTools ffmpeg)
{
    private const int MinUsefulRows = 64;
    private const ushort OrientationTag = 0x0112;

    public async Task<PhotoRecoveryResult> RecoverAsync(string input, string outputDirectory, CancellationToken ct)
    {
        var data = await File.ReadAllBytesAsync(input, ct).ConfigureAwait(false);
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return new PhotoRecoveryResult(false, "not a JPEG; only JPEG salvage is supported", []);

        Directory.CreateDirectory(outputDirectory);
        var stem = Path.GetFileNameWithoutExtension(input).Trim();
        var transform = OrientationFilter(ReadOrientation(input));
        var outputs = new List<string>();
        var notes = new List<string>();
        var tempDir = Path.Combine(Path.GetTempPath(), "mediasorter-photo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var preview = await ExtractPreviewAsync(data, tempDir, ct).ConfigureAwait(false);
            if (preview is { } p)
            {
                var path = UniquePath(outputDirectory, $"{stem}_preview.jpg");
                if (await EncodeAsync(p.Path, transform, null, path, ct).ConfigureAwait(false))
                {
                    outputs.Add(path);
                    notes.Add($"preview {p.Width}x{p.Height}");
                }
            }

            var header = JpegHeader.Parse(data);
            if (header is { } h)
            {
                int intactRows = await FindIntactRowsAsync(input, h, ct).ConfigureAwait(false);
                if (intactRows >= h.Height - h.McuHeight)
                {
                    // Decoder complained but every row looks like real picture: keep a clean re-encode.
                    var path = UniquePath(outputDirectory, $"{stem}_decoded.jpg");
                    if (await EncodeAsync(input, transform, null, path, ct).ConfigureAwait(false))
                    {
                        outputs.Add(path);
                        notes.Add("full image (minor decode errors)");
                    }
                }
                else if (intactRows >= MinUsefulRows)
                {
                    var path = UniquePath(outputDirectory, $"{stem}_partial.jpg");
                    if (await EncodeAsync(input, transform, $"crop=iw:{intactRows}:0:0", path, ct).ConfigureAwait(false))
                    {
                        outputs.Add(path);
                        notes.Add($"top {intactRows}/{h.Height} px ({100.0 * intactRows / h.Height:F0}%)");
                    }
                }
                else
                {
                    notes.Add("no intact rows in main image");
                }
            }

            var timestamp = File.GetLastWriteTime(input);
            foreach (var output in outputs)
                File.SetLastWriteTime(output, timestamp);

            return outputs.Count > 0
                ? new PhotoRecoveryResult(true, string.Join(", ", notes), outputs)
                : new PhotoRecoveryResult(false, notes.Count > 0 ? string.Join(", ", notes) : "nothing salvageable", outputs);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    private async Task<(string Path, int Width, int Height)?> ExtractPreviewAsync(byte[] data, string tempDir, CancellationToken ct)
    {
        (string Path, int Width, int Height)? best = null;
        int index = 0;
        foreach (var (start, length) in EmbeddedJpegs(data))
        {
            if (JpegHeader.Parse(data.AsSpan(start, length)) is not { } h || h.Width < 32 || h.Height < 32)
                continue;
            if (best is { } b && h.Width * h.Height <= b.Width * b.Height)
                continue;
            var path = Path.Combine(tempDir, $"preview{index++}.jpg");
            await File.WriteAllBytesAsync(path, data[start..(start + length)], ct).ConfigureAwait(false);
            if ((await ffmpeg.DecodeImageErrorsAsync(path, ct).ConfigureAwait(false)).Count == 0)
                best = (path, h.Width, h.Height);
        }
        return best;
    }

    /// <summary>Complete JPEG streams nested inside the APPn segments of the outer file.</summary>
    private static IEnumerable<(int Start, int Length)> EmbeddedJpegs(byte[] d)
    {
        int p = 2;
        while (p + 4 <= d.Length && d[p] == 0xFF)
        {
            byte marker = d[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (marker is 0xDA or 0xD9) yield break;
            int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 2));
            int segEnd = Math.Min(d.Length, p + 2 + len);
            if (marker is >= 0xE0 and <= 0xEF)
            {
                int s = p + 4;
                while ((s = IndexOf(d, s, segEnd, [0xFF, 0xD8, 0xFF])) >= 0)
                {
                    int e = IndexOf(d, s + 3, segEnd, [0xFF, 0xD9]);
                    if (e < 0)
                        break;
                    yield return (s, e + 2 - s);
                    s = e + 2;
                }
            }
            p = segEnd;
        }
    }

    /// <summary>
    /// Decodes the image leniently (luma only) and returns how many rows from the top are real
    /// picture: stops at the first MCU-row boundary with a discontinuity far above the local
    /// texture level, or at the start of the flat fill the decoder emits once data runs out.
    /// </summary>
    private async Task<int> FindIntactRowsAsync(string input, JpegHeader h, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(ffmpeg.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "quiet", "-noautorotate", "-f", "image2", "-i", input,
             "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "-"], null, ct, binaryStdout: true).ConfigureAwait(false);
        var luma = r.StdOutBytes;
        if (luma is null || luma.Length != h.Width * h.Height)
            return 0;
        return IntactRowAnalyzer.IntactRows(luma, h.Width, h.Height, h.McuHeight);
    }

    private async Task<bool> EncodeAsync(string input, string? orientation, string? crop, string output, CancellationToken ct)
    {
        var filters = string.Join(",", new[] { crop, orientation }.Where(f => !string.IsNullOrEmpty(f)));
        var args = new List<string> { "-v", "error", "-y", "-noautorotate", "-f", "image2", "-i", input, "-frames:v", "1" };
        if (filters.Length > 0)
            args.AddRange(["-vf", filters]);
        args.AddRange(["-q:v", "2", "-update", "1", output]);
        await ffmpeg.RunFfmpegAsync(args, ct).ConfigureAwait(false);
        return File.Exists(output) && new FileInfo(output).Length > 0;
    }

    private static int ReadOrientation(string path)
    {
        try
        {
            foreach (var dir in ImageMetadataReader.ReadMetadata(path))
                if (dir.Name == "Exif IFD0" && dir.TryGetInt32(OrientationTag, out int o))
                    return o;
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException)
        {
        }
        return 1;
    }

    private static string? OrientationFilter(int orientation) => orientation switch
    {
        2 => "hflip",
        3 => "hflip,vflip",
        4 => "vflip",
        5 => "transpose=0",
        6 => "transpose=1",
        7 => "transpose=3",
        8 => "transpose=2",
        _ => null
    };

    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int n = 1; File.Exists(path); n++)
            path = Path.Combine(dir, $"{stem} ({n}){ext}");
        return path;
    }

    private static int IndexOf(byte[] d, int from, int end, ReadOnlySpan<byte> pattern)
    {
        if (from >= end)
            return -1;
        int i = d.AsSpan(from, end - from).IndexOf(pattern);
        return i < 0 ? -1 : from + i;
    }
}

internal readonly record struct JpegHeader(int Width, int Height, int McuHeight)
{
    public static JpegHeader? Parse(ReadOnlySpan<byte> d)
    {
        int p = 2;
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF)
                return null;
            byte m = d[p + 1];
            if (m == 0xFF) { p++; continue; }
            if (m is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { p += 2; continue; }
            int len = BinaryPrimitives.ReadUInt16BigEndian(d[(p + 2)..]);
            bool isSof = m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC);
            if (isSof && p + 10 <= d.Length)
            {
                int height = BinaryPrimitives.ReadUInt16BigEndian(d[(p + 5)..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(d[(p + 7)..]);
                int components = d[p + 9];
                int maxV = 1;
                for (int c = 0; c < components && p + 12 + c * 3 < d.Length; c++)
                    maxV = Math.Max(maxV, d[p + 11 + c * 3] & 0x0F);
                return width > 0 && height > 0 ? new JpegHeader(width, height, 8 * maxV) : null;
            }
            if (m == 0xDA)
                return null;
            p += 2 + len;
        }
        return null;
    }
}

public static class IntactRowAnalyzer
{
    private const double FlatBandStdDev = 1.5;
    private const double MinCrossJump = 12;

    public static int IntactRows(byte[] luma, int width, int height, int mcu)
    {
        int bands = height / mcu;
        int end = bands;
        while (end > 0 && BandStdDev(luma, width, (end - 1) * mcu, mcu) < FlatBandStdDev)
            end--;
        if (end <= 1)
            return end * mcu;

        var cross = new double[end];
        var intra = new double[end];
        for (int b = 1; b < end; b++)
        {
            int y = b * mcu;
            cross[b] = RowDiff(luma, width, y, y - 1);
            var samples = new List<double>();
            for (int k = 2; k < mcu - 1; k += 3)
            {
                if (y + k < height) samples.Add(RowDiff(luma, width, y + k, y + k - 1));
                samples.Add(RowDiff(luma, width, y - k, y - k - 1));
            }
            intra[b] = Median(samples);
        }

        int head = Math.Max(3, (end - 1) / 10);
        double baseline = Median(intra.Skip(1).Take(head).ToList());
        for (int b = 1; b < end; b++)
        {
            if (cross[b] > Math.Max(Math.Max(6 * intra[b], 3 * baseline), MinCrossJump))
                return b * mcu;
        }
        return end * mcu;
    }

    private static double RowDiff(byte[] g, int width, int y0, int y1)
    {
        long sum = 0;
        int a = y0 * width, b = y1 * width;
        for (int x = 0; x < width; x++)
            sum += Math.Abs(g[a + x] - g[b + x]);
        return (double)sum / width;
    }

    private static double BandStdDev(byte[] g, int width, int y, int rows)
    {
        double sum = 0, sumSq = 0;
        long n = 0;
        for (int r = y; r < y + rows; r += 2)
        for (int x = 0; x < width; x += 4)
        {
            double v = g[r * width + x];
            sum += v;
            sumSq += v * v;
            n++;
        }
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        values.Sort();
        return values[values.Count / 2];
    }
}
