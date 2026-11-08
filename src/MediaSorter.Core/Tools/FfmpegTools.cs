using System.Globalization;
using MediaSorter.Core.Analysis;

namespace MediaSorter.Core.Tools;

public sealed class FfmpegTools
{
    public string Ffmpeg { get; }
    public string Ffprobe { get; }

    private FfmpegTools(string ffmpeg, string ffprobe)
    {
        Ffmpeg = ffmpeg;
        Ffprobe = ffprobe;
    }

    public static FfmpegTools? Locate(string? configuredDirectory = null)
    {
        foreach (var dir in CandidateDirectories(configuredDirectory))
        {
            var ffmpeg = Path.Combine(dir, ExeName("ffmpeg"));
            var ffprobe = Path.Combine(dir, ExeName("ffprobe"));
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                return new FfmpegTools(ffmpeg, ffprobe);
        }
        return null;
    }

    public Task<ProcessResult> RunFfmpegAsync(IEnumerable<string> args, CancellationToken ct, byte[]? stdin = null) =>
        ProcessRunner.RunAsync(Ffmpeg, ["-hide_banner", "-nostdin", .. args], stdin, ct);

    public async Task<HealthReport> ProbeAsync(string path, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(Ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path], null, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            return HealthReport.Broken(FirstLine(r.StdErr, "ffprobe failed"));
        if (!double.TryParse(r.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || duration <= 0)
            return HealthReport.Broken("no playable duration");
        return HealthReport.Ok;
    }

    /// <summary>Fully decodes a still image and returns the decoder's complaints (empty = clean).</summary>
    public async Task<IReadOnlyList<string>> DecodeImageErrorsAsync(string path, CancellationToken ct, bool ignoreTruncation = false)
    {
        var r = await RunFfmpegAsync(["-v", "error", "-noautorotate", "-f", "image2", "-i", path, "-frames:v", "1", "-f", "null", "-"], ct).ConfigureAwait(false);
        return r.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.Contains("APP fields", StringComparison.Ordinal))
            .Where(l => !ignoreTruncation || !l.Contains("overread", StringComparison.Ordinal))
            .ToList();
    }

    public async Task<HealthReport> VerifyImageAsync(string path, CancellationToken ct)
    {
        var errors = await DecodeImageErrorsAsync(path, ct).ConfigureAwait(false);
        if (errors.Count == 0)
            return HealthReport.Ok;
        bool truncated = errors.Any(e => e.Contains("overread", StringComparison.Ordinal) || e.Contains("EOF", StringComparison.Ordinal));
        return HealthReport.Broken(truncated ? "image data ends early (truncated)" : "corrupt image data (decoder errors)");
    }

    public async Task<int> CountVideoFramesAsync(string path, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", path], null, ct).ConfigureAwait(false);
        return int.TryParse(r.StdOut.Trim().TrimEnd(','), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static string FirstLine(string text, string fallback)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? fallback : line;
    }

    private static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static IEnumerable<string> CandidateDirectories(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg");

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return dir.Trim('"');

        if (!OperatingSystem.IsWindows())
            yield break;

        var wingetPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (System.IO.Directory.Exists(wingetPackages))
        {
            foreach (var pkg in System.IO.Directory.EnumerateDirectories(wingetPackages, "*FFmpeg*"))
            foreach (var bin in System.IO.Directory.EnumerateDirectories(pkg, "bin", SearchOption.AllDirectories))
                yield return bin;
        }
        yield return @"C:\ffmpeg\bin";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin");
    }
}
