using System.Globalization;
using MediaSorter.Core.Analysis;
using MediaSorter.Core.Planning;
using MediaSorter.Core.Recovery;
using MediaSorter.Core.Tools;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return args.FirstOrDefault()?.ToLowerInvariant() switch
    {
        "inspect" => await Inspect(args.Skip(1).ToArray(), cts.Token),
        "plan" => await Sort(args.Skip(1).ToArray(), execute: false, cts.Token),
        "run" => await Sort(args.Skip(1).ToArray(), execute: true, cts.Token),
        "undo" => await Undo(args.Skip(1).ToArray(), cts.Token),
        "recover" => await Recover(args.Skip(1).ToArray(), cts.Token),
        _ => Usage()
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static int Usage()
{
    Console.WriteLine("""
        mediasorter inspect <file>...
        mediasorter plan    --source <dir> [--source <dir>...] --target <dir> [options]
        mediasorter run     --source <dir> [--source <dir>...] --target <dir> [options]
        mediasorter undo    --target <dir> [--journal <file>]
        mediasorter recover <file|dir>... [--out-dir <dir>] [--fps <rate>] [--size <WxH>] [--rotate 90|180|270]
                            videos: rebuild moov-less H.264 MP4/MOV
                            photos: extract embedded preview + intact top region of damaged JPEGs

        options:
          --template <t>     folder layout, default "{Year}/{Kind}/{Month}"
                             tokens: {Year} {Month} {MonthNum} {MonthName} {Day} {Kind} {Source} {Format}
          --move             move instead of copy
          --keep-extensions  don't fix extensions that don't match the real format
          --keep-duplicates  don't skip byte-identical files
          --include-unknown  copy unrecognized files into Other/
          --deep-verify      probe every video with ffprobe
          --no-photo-verify  skip full decode of photos (faster, misses corrupt image data)
          --ffmpeg <dir>     directory containing ffmpeg/ffprobe
        """);
    return 1;
}

static async Task<int> Inspect(string[] files, CancellationToken ct)
{
    var verify = new VerifyOptions(FfmpegTools.Locate(), DecodePhotos: true, ProbeVideos: true);
    foreach (var file in files)
    {
        var info = await Planner.AnalyzeAsync(Path.GetFullPath(file), verify, ct);
        Console.WriteLine(file);
        Console.WriteLine($"  type    : {info.Type.Kind} / {info.Type.Format} (expected extension {info.Type.Extension}{(FileTypeDetector.ExtensionMatches(file, info.Type) ? "" : ", MISMATCH")})");
        Console.WriteLine($"  date    : {info.Date.Value:yyyy-MM-dd HH:mm:ss} from {info.Date.Source} ({info.Date.Detail})");
        Console.WriteLine($"  health  : {(info.Health.IsBroken ? "BROKEN - " + info.Health.Reason : "ok")}");
    }
    return 0;
}

static async Task<int> Sort(string[] args, bool execute, CancellationToken ct)
{
    var a = new Args(args);
    var options = new SortOptions
    {
        Sources = a.All("--source"),
        TargetRoot = a.Required("--target"),
        Template = a.Value("--template") ?? LayoutTemplate.Default,
        Move = a.Has("--move"),
        FixExtensions = !a.Has("--keep-extensions"),
        SkipDuplicates = !a.Has("--keep-duplicates"),
        IncludeUnknown = a.Has("--include-unknown"),
        DeepVerifyVideos = a.Has("--deep-verify"),
        VerifyPhotosByDecoding = !a.Has("--no-photo-verify"),
        FfmpegDirectory = a.Value("--ffmpeg")
    };
    if (options.Sources.Count == 0)
        throw new ArgumentException("At least one --source is required.");

    var progress = new ConsoleProgress();
    var plan = await new Planner().BuildAsync(options, progress, ct);
    progress.Finish();

    foreach (var item in plan.Items)
    {
        var dest = item.Destination is null ? "" : Path.GetRelativePath(options.TargetRoot, item.Destination);
        Console.WriteLine($"{item.Action,-14} {item.Media.Date.Value:yyyy-MM-dd} {item.Media.Date.Source,-10} {Path.GetFileName(item.SourcePath)} -> {dest} {(item.Note.Length > 0 ? "[" + item.Note + "]" : "")}");
    }
    Console.WriteLine();
    Console.WriteLine($"gallery {plan.Count(PlanCategory.Gallery)}, broken {plan.Count(PlanCategory.Broken)}, other {plan.Count(PlanCategory.Other)}, " +
                      $"duplicates {plan.Count(PlanAction.SkipDuplicate)}, unknown {plan.Count(PlanAction.SkipUnknown)}, errors {plan.Count(PlanAction.SkipError)}, " +
                      $"{plan.ActionableBytes / 1024.0 / 1024.0:N1} MB to {(options.Move ? "move" : "copy")}");

    if (!execute)
        return 0;

    var result = await Executor.ExecuteAsync(plan, progress, ct);
    progress.Finish();
    foreach (var error in result.Errors)
        Console.Error.WriteLine(error);
    Console.WriteLine($"done: {result.Succeeded} ok, {result.Failed} failed. journal: {result.JournalPath}");
    return result.Failed == 0 ? 0 : 3;
}

static async Task<int> Undo(string[] args, CancellationToken ct)
{
    var a = new Args(args);
    var journal = a.Value("--journal") ?? Executor.LatestJournal(a.Required("--target"))
                  ?? throw new ArgumentException("No journal found.");
    var progress = new ConsoleProgress();
    var result = await Executor.UndoAsync(journal, progress, ct);
    progress.Finish();
    foreach (var error in result.Errors)
        Console.Error.WriteLine(error);
    Console.WriteLine($"undone: {result.Succeeded}, failed: {result.Failed}");
    return result.Failed == 0 ? 0 : 3;
}

static async Task<int> Recover(string[] args, CancellationToken ct)
{
    var a = new Args(args);
    if (a.Positional.Count == 0)
        throw new ArgumentException("recover needs at least one file or directory.");
    var ffmpeg = FfmpegTools.Locate(a.Value("--ffmpeg")) ?? throw new ArgumentException("ffmpeg/ffprobe not found. Install ffmpeg or pass --ffmpeg <dir>.");

    (int, int)? size = null;
    if (a.Value("--size") is { } s)
    {
        var parts = s.Split('x', 'X');
        size = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }
    var videoOptions = new RecoveryOptions
    {
        FrameRate = a.Value("--fps") is { } fps ? double.Parse(fps, CultureInfo.InvariantCulture) : null,
        Resolution = size,
        RotateClockwise = a.Value("--rotate") is { } rot ? int.Parse(rot, CultureInfo.InvariantCulture) : 0
    };

    var inputs = a.Positional.SelectMany(p => Directory.Exists(p)
            ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
            : [p])
        .Select(Path.GetFullPath)
        .ToList();
    var outDir = a.Value("--out-dir");
    var photoRecovery = new PhotoRecovery(ffmpeg);
    var videoRecovery = new VideoRecovery(ffmpeg);
    int ok = 0, failed = 0;
    var gate = new object();

    var photos = inputs.Where(i => FileTypeDetector.Detect(i).Kind == MediaKind.Photo).ToList();
    var videos = inputs.Where(i => FileTypeDetector.Detect(i).Kind == MediaKind.Video).ToList();

    var verify = new VerifyOptions(ffmpeg, DecodePhotos: true, ProbeVideos: false);
    int healthy = 0;
    await Parallel.ForEachAsync(photos, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct }, async (input, token) =>
    {
        var type = FileTypeDetector.Detect(input);
        if (!(await HealthChecker.CheckAsync(input, type, verify, token)).IsBroken)
        {
            Interlocked.Increment(ref healthy);
            return;
        }
        var dir = outDir ?? Path.Combine(Path.GetDirectoryName(input)!, "Recovered");
        var result = await photoRecovery.RecoverAsync(input, Path.Combine(dir, "Photos"), token);
        lock (gate)
        {
            if (result.Success) ok++; else failed++;
            Console.WriteLine($"{(result.Success ? "OK  " : "FAIL")} {Path.GetFileName(input)}: {result.Message}");
        }
    });
    if (healthy > 0)
        Console.WriteLine($"skipped {healthy} healthy photos");

    foreach (var input in videos)
    {
        var dir = Path.Combine(outDir ?? Path.Combine(Path.GetDirectoryName(input)!, "Recovered"), "Videos");
        var output = Path.Combine(dir, Path.GetFileNameWithoutExtension(input) + "_recovered.mp4");
        var result = await videoRecovery.RecoverAsync(input, output, videoOptions, new Progress<string>(Console.WriteLine), ct);
        if (result.Success) ok++; else failed++;
        Console.WriteLine(result.Success ? $"OK   {output}" : $"FAIL {Path.GetFileName(input)}: {result.Message}");
    }

    Console.WriteLine($"recovered {ok}, nothing salvageable {failed}");
    return ok > 0 || failed == 0 ? 0 : 4;
}

sealed class Args
{
    private readonly List<(string Key, string? Value)> _named = [];
    public List<string> Positional { get; } = [];

    private static readonly HashSet<string> Switches = ["--move", "--keep-extensions", "--keep-duplicates", "--include-unknown", "--deep-verify", "--no-photo-verify"];

    public Args(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                Positional.Add(args[i]);
            else if (Switches.Contains(args[i]))
                _named.Add((args[i], null));
            else if (i + 1 < args.Length)
                _named.Add((args[i], args[++i]));
            else
                throw new ArgumentException($"{args[i]} needs a value.");
        }
    }

    public bool Has(string key) => _named.Any(n => n.Key == key);
    public string? Value(string key) => _named.LastOrDefault(n => n.Key == key).Value;
    public List<string> All(string key) => _named.Where(n => n.Key == key && n.Value is not null).Select(n => n.Value!).ToList();
    public string Required(string key) => Value(key) ?? throw new ArgumentException($"{key} is required.");
}

sealed class ConsoleProgress : IProgress<ProgressInfo>
{
    private readonly object _gate = new();
    private string _last = "";

    public void Report(ProgressInfo value)
    {
        lock (_gate)
        {
            var line = value.Total > 0 ? $"{value.Stage} {value.Done}/{value.Total}" : value.Stage;
            if (line == _last || Console.IsOutputRedirected)
                return;
            _last = line;
            Console.Write($"\r{line,-60}");
        }
    }

    public void Finish()
    {
        if (!Console.IsOutputRedirected)
            Console.WriteLine();
    }
}
