using System.Collections.Concurrent;
using System.Globalization;
using MediaSorter.Core.Tools;

namespace MediaSorter.Core.Recovery;

public sealed record RecoveryOptions
{
    public double? FrameRate { get; init; }
    public (int Width, int Height)? Resolution { get; init; }
    public int MaxParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Clockwise display rotation for the output; the original rotation lived in the lost moov.</summary>
    public int RotateClockwise { get; init; }

    public static readonly IReadOnlyList<(int Width, int Height)> CommonResolutions =
    [
        (1920, 1080), (3840, 2160), (1280, 720), (2560, 1440), (4096, 2160), (1440, 1080),
        (1080, 1920), (720, 1280), (2160, 3840), (960, 540), (854, 480), (720, 480), (720, 576),
        (640, 480), (640, 360), (480, 360), (352, 288), (320, 240), (176, 144), (3264, 1836), (2704, 1520)
    ];
}

public sealed record RecoveryResult(
    bool Success,
    string Message,
    string? OutputPath,
    StreamParameters? Parameters,
    double FrameRate,
    int Frames,
    bool HasAudio,
    IReadOnlyList<string> Log);

/// <summary>
/// Rebuilds a playable MP4 from a QuickTime/MP4 whose moov atom was never written or got lost
/// (interrupted recordings, carved files from data recovery). H.264 parameter sets that only
/// lived in moov are reconstructed from slice headers and verified by trial decoding.
/// </summary>
public sealed class VideoRecovery(FfmpegTools ffmpeg)
{
    private const int StageBExtraNals = 40;
    private const int ProbeSlices = 40;
    private const int MaxHeaderCandidates = 12;
    private const int StageBCombos = MaxHeaderCandidates;
    private const long MaxInputBytes = 1_900_000_000;

    private static readonly double[] StandardFrameRates = [15, 24, 25, 30, 48, 50, 60, 120, 240];
    private static readonly int[] StandardSampleRates = [48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000];

    private readonly DecodeOracle _oracle = new(ffmpeg);

    public async Task<RecoveryResult> RecoverAsync(string input, string outputPath, RecoveryOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        var log = new List<string>();
        void Log(string message)
        {
            log.Add(message);
            progress?.Report(message);
        }
        RecoveryResult Fail(string message)
        {
            Log(message);
            return new RecoveryResult(false, message, null, null, 0, 0, false, log);
        }

        var length = new FileInfo(input).Length;
        if (length > MaxInputBytes)
            return Fail($"File is too large for in-memory recovery ({length:N0} bytes).");

        var data = await File.ReadAllBytesAsync(input, ct).ConfigureAwait(false);
        var boxes = IsoBmff.ReadTopLevel(data);
        Log($"Atoms: {string.Join(", ", boxes.Select(b => b.IsTruncated ? $"{b.Type}({b.Size:N0}/{b.DeclaredSize:N0})" : $"{b.Type}({b.Size:N0})"))}");

        var tempDir = Path.Combine(Path.GetTempPath(), "mediasorter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            if (boxes.Any(b => b.Type == "moov" && !b.IsTruncated))
            {
                Log("moov atom present, trying to remux the decodable part.");
                var salvaged = await TrySalvageRemuxAsync(input, outputPath, ct).ConfigureAwait(false);
                if (salvaged > 0)
                    return new RecoveryResult(true, $"Remuxed {salvaged} decodable frames.", outputPath, null, 0, salvaged, false, log);
                Log("Remux produced no decodable frames.");
            }

            var mdat = boxes.FirstOrDefault(b => b.Type == "mdat");
            int start = mdat is null ? 0 : (int)mdat.PayloadOffset;
            int end = mdat is null ? data.Length : (int)mdat.End;

            switch (NalScanner.Identify(data, start, end))
            {
                case VideoCodecGuess.Hevc:
                    return Fail("Video is HEVC (H.265); rebuilding HEVC parameter sets is not supported yet.");
                case VideoCodecGuess.None:
                    return Fail("No recognizable H.264 stream inside the file (codec may be legacy, e.g. Sorenson/MPEG-4 Part 2, or the data is encrypted/overwritten).");
            }

            var scan = NalScanner.ScanAvc(data, start, end)!;
            Log($"H.264 stream: {scan.Nals.Count} NAL units, {scan.VideoBytes:N0} bytes, {scan.Gaps.Count} non-video chunks.");

            int firstIdr = IndexOf(scan.Nals, n => NalScanner.NalType(data[n.Offset]) == 5);
            if (firstIdr < 0)
                return Fail("No keyframe (IDR) found; nothing can be decoded without one.");

            var videoNals = scan.Nals.Skip(firstIdr)
                .Where(n => NalScanner.NalType(data[n.Offset]) is 1 or 5)
                .ToList();
            int frameCount = videoNals.Count(n => (data[n.Offset + 1] & 0x80) != 0);
            var gop = MedianGop(data, videoNals);
            Log($"{frameCount} frames from first keyframe, keyframe interval {gop?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.");

            byte[] sps, pps;
            StreamParameters? parameters = null;
            var inbandSps = scan.Nals.FirstOrDefault(n => NalScanner.NalType(data[n.Offset]) == 7);
            var inbandPps = scan.Nals.FirstOrDefault(n => NalScanner.NalType(data[n.Offset]) == 8);
            if (inbandSps.Length > 0 && inbandPps.Length > 0)
            {
                Log("Stream carries its own SPS/PPS; no inference needed.");
                sps = data.AsSpan(inbandSps.Offset, inbandSps.Length).ToArray();
                pps = data.AsSpan(inbandPps.Offset, inbandPps.Length).ToArray();
            }
            else
            {
                parameters = await InferParametersAsync(data, videoNals, options, Log, ct).ConfigureAwait(false);
                if (parameters is null)
                    return Fail("Could not reconstruct the H.264 parameter sets.");
                sps = ParameterSets.BuildSps(parameters.Header, parameters.Profile, parameters.Width, parameters.Height);
                pps = ParameterSets.BuildPps(parameters.Header, parameters.Profile, parameters.Qp);
            }

            var h264Path = Path.Combine(tempDir, "video.h264");
            await using (var fs = File.Create(h264Path))
            {
                foreach (var nal in new[] { sps, pps }.Concat(videoNals.Select(n => data.AsSpan(n.Offset, n.Length).ToArray())))
                {
                    await fs.WriteAsync(new byte[] { 0, 0, 0, 1 }, ct).ConfigureAwait(false);
                    await fs.WriteAsync(nal, ct).ConfigureAwait(false);
                }
            }

            var audioChunks = scan.Gaps.Where(g => g.Length >= 64).ToList();
            var audio = audioChunks.Count > 0
                ? await RecoverAudioAsync(data, audioChunks, tempDir, Log, ct).ConfigureAwait(false)
                : null;

            double fps = options.FrameRate ?? ChooseFrameRate(gop, frameCount, audio?.Samples48k);
            string? wavPath = null;
            if (audio is not null)
            {
                int sampleRate = SnapSampleRate(audio.Samples48k * fps / frameCount);
                wavPath = sampleRate == 48000
                    ? audio.WavPath
                    : (await DecodeAudioAsync(data, audioChunks, sampleRate, audio.Channels, tempDir, ct).ConfigureAwait(false))?.WavPath;
                Log($"Audio: AAC-LC {sampleRate} Hz, {audio.Channels} ch, {(double)audio.Samples48k / sampleRate:F1} s.");
            }
            Log($"Frame rate: {fps.ToString("0.###", CultureInfo.InvariantCulture)} fps{(options.FrameRate is null ? " (estimated)" : "")}.");

            await MuxAsync(h264Path, wavPath, fps, options.RotateClockwise, outputPath, tempDir, ct).ConfigureAwait(false);
            int decoded = await ffmpeg.CountVideoFramesAsync(outputPath, ct).ConfigureAwait(false);
            if (decoded == 0)
                return Fail("Rebuilt file does not decode.");

            var message = $"Recovered {decoded} frames ({decoded / fps:F1} s){(wavPath is not null ? " with audio" : "")}.";
            Log(message);
            return new RecoveryResult(true, message, outputPath, parameters, fps, decoded, wavPath is not null, log);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    private async Task<StreamParameters?> InferParametersAsync(byte[] data, List<NalUnit> videoNals, RecoveryOptions options, Action<string> log, CancellationToken ct)
    {
        var headerCandidates = SliceHeaderProbe.Probe(data, videoNals.Take(ProbeSlices).ToList(), MaxHeaderCandidates);
        if (headerCandidates.Count == 0)
        {
            log("No consistent slice-header configuration found.");
            return null;
        }
        log($"Slice header analysis: {headerCandidates.Count} candidate(s), best: {headerCandidates[0]}.");

        var idr = videoNals[0];
        var idrHead = data.AsMemory(idr.Offset, idr.Length);
        const int probeWidth = 1920, probeHeight = 1080;

        var jobs = new List<(int Order, HeaderConfig Header, ProfileConfig Profile, int Qp)>();
        foreach (var h in headerCandidates)
        foreach (var profile in ProfilesFor(h))
        foreach (var qp in QpOrder())
            jobs.Add((jobs.Count, h, profile, qp));

        log($"Searching profile/QP space ({jobs.Count} trial decodes)...");
        var stageA = new ConcurrentBag<(int Order, HeaderConfig Header, ProfileConfig Profile, int Qp, long Rank)>();
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = ct }, async (job, token) =>
        {
            var stream = ParameterSets.AnnexB([
                ParameterSets.BuildSps(job.Header, job.Profile, probeWidth, probeHeight),
                ParameterSets.BuildPps(job.Header, job.Profile, job.Qp),
                idrHead]);
            var score = await _oracle.ScoreAsync(stream, (probeWidth + 15) / 16, token).ConfigureAwait(false);
            stageA.Add((job.Order, job.Header, job.Profile, job.Qp, score.Rank));
        }).ConfigureAwait(false);

        // Slice data parses identically for header variants that only differ in later fields, so keep
        // the best profile/QP per header candidate and let the multi-frame decode in stage B pick.
        var finalists = stageA
            .GroupBy(s => s.Header)
            .Select(g => g.OrderByDescending(s => s.Rank).ThenBy(s => s.Order).First())
            .OrderByDescending(s => s.Rank).ThenBy(s => s.Order)
            .Take(StageBCombos)
            .ToList();
        log($"Best profile/QP: {finalists[0].Profile}, QP {finalists[0].Qp} ({(finalists[0].Rank == long.MaxValue ? "keyframe probe decoded cleanly" : $"{finalists[0].Rank} MBs before first error")}).");

        var sample = videoNals.Take(1 + StageBExtraNals).Select(n => (ReadOnlyMemory<byte>)data.AsMemory(n.Offset, n.Length)).ToList();
        IReadOnlyList<(int Width, int Height)> resolutions = options.Resolution is { } forced ? [forced] : RecoveryOptions.CommonResolutions;

        StreamParameters? fallback = null;
        int fallbackErrors = int.MaxValue;
        foreach (var f in finalists)
        {
            var results = new ConcurrentDictionary<(int, int), DecodeScore>();
            await Parallel.ForEachAsync(resolutions, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = ct }, async (res, token) =>
            {
                var stream = ParameterSets.AnnexB(new ReadOnlyMemory<byte>[]
                {
                    ParameterSets.BuildSps(f.Header, f.Profile, res.Width, res.Height),
                    ParameterSets.BuildPps(f.Header, f.Profile, f.Qp)
                }.Concat(sample));
                results[res] = await _oracle.ScoreAsync(stream, (res.Width + 15) / 16, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

            foreach (var res in resolutions)
            {
                var score = results[res];
                var candidate = new StreamParameters(f.Header, f.Profile, f.Qp, res.Width, res.Height);
                if (score.Clean)
                {
                    log($"Verified: {candidate}.");
                    return candidate;
                }
                if (score.ErrorLines < fallbackErrors)
                {
                    fallbackErrors = score.ErrorLines;
                    fallback = candidate;
                }
            }
        }

        if (fallback is not null)
            log($"No error-free configuration; using closest match {fallback} ({fallbackErrors} decoder errors). Expect artifacts.");
        return fallback;
    }

    private sealed record AudioResult(string WavPath, long Samples48k, int Channels, int ErrorLines);

    private async Task<AudioResult?> RecoverAudioAsync(byte[] data, List<ByteRange> chunks, string tempDir, Action<string> log, CancellationToken ct)
    {
        AudioResult? best = null;
        foreach (int channels in new[] { 2, 1 })
        {
            var result = await DecodeAudioAsync(data, chunks, 48000, channels, tempDir, ct).ConfigureAwait(false);
            if (result is null)
                continue;
            if (best is null || result.ErrorLines < best.ErrorLines)
                best = result;
            if (result.ErrorLines == 0)
                break;
        }

        if (best is null || best.Samples48k == 0 || best.ErrorLines > chunks.Count)
        {
            log($"Non-video chunks ({chunks.Count}) did not decode as AAC; output will be silent.");
            return null;
        }
        return best;
    }

    private async Task<AudioResult?> DecodeAudioAsync(byte[] data, List<ByteRange> chunks, int sampleRate, int channels, string tempDir, CancellationToken ct)
    {
        var mkv = Path.Combine(tempDir, $"audio-{sampleRate}-{channels}.mkv");
        var wav = Path.ChangeExtension(mkv, ".wav");
        await File.WriteAllBytesAsync(mkv, MatroskaAacWriter.Build(data, chunks, sampleRate, channels), ct).ConfigureAwait(false);
        var r = await ffmpeg.RunFfmpegAsync(["-v", "error", "-y", "-i", mkv, "-c:a", "pcm_s16le", "-f", "wav", wav], ct).ConfigureAwait(false);
        if (!File.Exists(wav))
            return null;
        int errors = r.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        long samples = Math.Max(0, (new FileInfo(wav).Length - 44) / (2L * channels));
        return new AudioResult(wav, samples, channels, errors);
    }

    private async Task MuxAsync(string h264Path, string? wavPath, double fps, int rotateClockwise, string outputPath, string tempDir, CancellationToken ct)
    {
        var (num, den) = Rational(fps);
        var videoOnly = Path.Combine(tempDir, "video.mp4");
        var r = await ffmpeg.RunFfmpegAsync(
            ["-v", "error", "-y", "-fflags", "+genpts", "-r", $"{num}/{den}", "-i", h264Path, "-c:v", "copy",
             "-bsf:v", "setts=ts=N", "-time_base", $"{den}/{num}", videoOnly], ct).ConfigureAwait(false);
        EnsureExists(videoOnly, r);

        var args = new List<string> { "-v", "error", "-y" };
        int rotation = ((rotateClockwise % 360) + 360) % 360;
        if (rotation != 0)
            args.AddRange(["-display_rotation:v:0", (-rotation).ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", videoOnly]);
        if (wavPath is not null)
            args.AddRange(["-i", wavPath, "-map", "0:v", "-map", "1:a", "-c:a", "aac", "-b:a", "192k", "-shortest"]);
        args.AddRange(["-c:v", "copy", "-movflags", "+faststart", outputPath]);
        r = await ffmpeg.RunFfmpegAsync(args, ct).ConfigureAwait(false);
        EnsureExists(outputPath, r);
    }

    private async Task<int> TrySalvageRemuxAsync(string input, string outputPath, CancellationToken ct)
    {
        await ffmpeg.RunFfmpegAsync(["-v", "error", "-y", "-err_detect", "ignore_err", "-i", input, "-c", "copy", "-movflags", "+faststart", outputPath], ct).ConfigureAwait(false);
        if (!File.Exists(outputPath))
            return 0;
        int frames = await ffmpeg.CountVideoFramesAsync(outputPath, ct).ConfigureAwait(false);
        if (frames == 0)
            File.Delete(outputPath);
        return frames;
    }

    private static void EnsureExists(string path, ProcessResult r)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"ffmpeg failed: {r.StdErr.Trim()}");
    }

    private static IEnumerable<ProfileConfig> ProfilesFor(HeaderConfig h) => h.Cabac
        ? [ProfileConfig.HighTransform8x8, ProfileConfig.High, ProfileConfig.Main]
        : [ProfileConfig.HighTransform8x8, ProfileConfig.High, ProfileConfig.Main, ProfileConfig.Baseline];

    private static IEnumerable<int> QpOrder()
    {
        yield return 26;
        for (int d = 1; d <= 26; d++)
        {
            if (26 + d <= 51) yield return 26 + d;
            if (26 - d >= 0) yield return 26 - d;
        }
    }

    private static int? MedianGop(byte[] data, List<NalUnit> videoNals)
    {
        var idrFrames = new List<int>();
        int frame = -1;
        foreach (var n in videoNals)
        {
            if ((data[n.Offset + 1] & 0x80) == 0)
                continue;
            frame++;
            if (NalScanner.NalType(data[n.Offset]) == 5)
                idrFrames.Add(frame);
        }
        if (idrFrames.Count < 2)
            return null;
        var gaps = idrFrames.Zip(idrFrames.Skip(1), (a, b) => b - a).Order().ToList();
        return gaps[gaps.Count / 2];
    }

    private static double ChooseFrameRate(int? gop, int frames, long? samples48k)
    {
        if (gop is { } g)
        {
            foreach (var rate in StandardFrameRates)
                if (Math.Abs(rate - g) < 0.5)
                    return rate;
        }
        if (samples48k is > 0 && frames > 0)
        {
            double estimate = frames * 48000.0 / samples48k.Value;
            var nearest = StandardFrameRates.MinBy(r => Math.Abs(r - estimate));
            if (Math.Abs(nearest - estimate) / nearest < 0.08)
                return nearest;
        }
        return 30;
    }

    private static int SnapSampleRate(double estimate) => StandardSampleRates.MinBy(r => Math.Abs(r - estimate));

    private static (int Num, int Den) Rational(double fps)
    {
        foreach (var ntsc in new[] { 24, 30, 60 })
            if (Math.Abs(fps - ntsc * 1000.0 / 1001) < 0.01)
                return (ntsc * 1000, 1001);
        return ((int)Math.Round(fps), 1);
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (int i = 0; i < list.Count; i++)
            if (predicate(list[i]))
                return i;
        return -1;
    }
}
