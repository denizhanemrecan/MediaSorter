using System.Collections.Concurrent;
using System.Security.Cryptography;
using MediaSorter.Core.Analysis;
using MediaSorter.Core.Tools;

namespace MediaSorter.Core.Planning;

public sealed class Planner
{
    public const string StateFolder = ".mediasorter";
    public const string RecoveredFolder = "Recovered";

    private readonly Dictionary<string, byte[]> _hashCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SortPlan> BuildAsync(SortOptions options, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (LayoutTemplate.Validate(options.Template) is { } error)
            throw new ArgumentException(error, nameof(options));

        var target = Path.GetFullPath(options.TargetRoot);
        var managedRoots = new[] { options.GalleryFolder, options.BrokenFolder, options.OtherFolder, StateFolder, RecoveredFolder }
            .Select(f => EnsureTrailingSeparator(Path.Combine(target, f)))
            .ToArray();

        progress?.Report(new ProgressInfo("Scanning", 0, 0));
        var files = EnumerateSources(options.Sources, managedRoots, ct);

        var ffmpeg = options.DeepVerifyVideos ? FfmpegTools.Locate(options.FfmpegDirectory) : null;
        var infos = new ConcurrentBag<MediaInfo>();
        int done = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = ct }, async (path, token) =>
        {
            infos.Add(await AnalyzeAsync(path, ffmpeg, token).ConfigureAwait(false));
            int n = Interlocked.Increment(ref done);
            if (n % 25 == 0 || n == files.Count)
                progress?.Report(new ProgressInfo("Analyzing", n, files.Count, path));
        }).ConfigureAwait(false);

        progress?.Report(new ProgressInfo("Planning", 0, infos.Count));
        var existingBySize = IndexExisting(managedRoots.Take(3), ct);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<PlanItem>(infos.Count);

        var ordered = infos
            .OrderBy(i => i.Health.IsBroken)
            .ThenBy(i => i.Date.Source)
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var info in ordered)
        {
            ct.ThrowIfCancellationRequested();
            items.Add(PlanOne(info, options, target, existingBySize, reserved));
        }

        progress?.Report(new ProgressInfo("Planned", items.Count, items.Count));
        return new SortPlan { Options = options, Items = items.OrderBy(i => i.Media.Path, StringComparer.OrdinalIgnoreCase).ToList() };
    }

    public static async Task<MediaInfo> AnalyzeAsync(string path, FfmpegTools? deepVerify, CancellationToken ct)
    {
        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
            var type = FileTypeDetector.Detect(path);
            var date = DateResolver.Resolve(path, type);
            var health = type.Kind == MediaKind.Unknown
                ? HealthReport.Ok
                : await HealthChecker.CheckAsync(path, type, deepVerify, ct).ConfigureAwait(false);
            return new MediaInfo(path, size, type, date, health);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MediaInfo(path, size, FileType.Unknown, new ResolvedDate(DateTime.MinValue, DateSource.FileSystem, ""), HealthReport.Broken(ex.Message), ex.Message);
        }
    }

    private PlanItem PlanOne(MediaInfo info, SortOptions o, string target, Dictionary<long, List<string>> existingBySize, HashSet<string> reserved)
    {
        if (info.Error is not null)
            return new PlanItem(info, PlanAction.SkipError, PlanCategory.None, null, info.Error);

        PlanCategory category;
        string folder;
        if (info.Type.Kind == MediaKind.Unknown)
        {
            if (!o.IncludeUnknown)
                return new PlanItem(info, PlanAction.SkipUnknown, PlanCategory.None, null, "not a recognized photo/video");
            category = PlanCategory.Other;
            folder = Path.Combine(target, o.OtherFolder);
        }
        else if (info.Health.IsBroken)
        {
            category = PlanCategory.Broken;
            folder = Path.Combine(target, o.BrokenFolder, info.Type.Kind == MediaKind.Video ? "Videos" : "Photos");
        }
        else
        {
            category = PlanCategory.Gallery;
            folder = Path.Combine(target, o.GalleryFolder,
                LayoutTemplate.Expand(o.Template, info.Date.Value, info.Type.Kind, info.Date.Source, info.Type.Format));
        }

        if (o.SkipDuplicates && FindDuplicate(info, existingBySize) is { } duplicateOf)
            return new PlanItem(info, PlanAction.SkipDuplicate, category, null, $"duplicate of {duplicateOf}");

        var notes = new List<string>();
        var fileName = Path.GetFileName(info.Path);
        if (o.FixExtensions && info.Type.Kind != MediaKind.Unknown && !FileTypeDetector.ExtensionMatches(info.Path, info.Type))
        {
            var fixedName = Path.GetFileNameWithoutExtension(info.Path) + info.Type.Extension;
            notes.Add($"extension {Path.GetExtension(info.Path)} -> {info.Type.Extension}");
            fileName = fixedName;
        }
        if (info.Health.IsBroken)
            notes.Add(info.Health.Reason);

        var destination = Reserve(Path.Combine(folder, fileName), reserved);
        if (!existingBySize.TryGetValue(info.Size, out var bucket))
            existingBySize[info.Size] = bucket = [];
        bucket.Add(info.Path);

        return new PlanItem(info, o.Move ? PlanAction.Move : PlanAction.Copy, category, destination, string.Join("; ", notes));
    }

    private string? FindDuplicate(MediaInfo info, Dictionary<long, List<string>> bySize)
    {
        if (info.Size == 0 || !bySize.TryGetValue(info.Size, out var candidates))
            return null;
        var hash = Hash(info.Path);
        if (hash is null)
            return null;
        foreach (var candidate in candidates)
            if (Hash(candidate) is { } other && other.AsSpan().SequenceEqual(hash))
                return candidate;
        return null;
    }

    private byte[]? Hash(string path)
    {
        if (_hashCache.TryGetValue(path, out var cached))
            return cached;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            var hash = SHA256.HashData(fs);
            _hashCache[path] = hash;
            return hash;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Reserve(string desired, HashSet<string> reserved)
    {
        var dir = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        var candidate = desired;
        for (int n = 1; reserved.Contains(candidate) || File.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
        reserved.Add(candidate);
        return candidate;
    }

    private static List<string> EnumerateSources(IEnumerable<string> sources, string[] excludedRoots, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var source in sources)
        {
            var root = Path.GetFullPath(source);
            if (File.Exists(root))
            {
                if (seen.Add(root)) result.Add(root);
                continue;
            }
            if (!System.IO.Directory.Exists(root))
                continue;
            foreach (var file in System.IO.Directory.EnumerateFiles(root, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (excludedRoots.Any(r => file.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (Path.GetFileName(file) is "desktop.ini" or "Thumbs.db" or ".DS_Store")
                    continue;
                if (seen.Add(file))
                    result.Add(file);
            }
        }
        return result;
    }

    private static Dictionary<long, List<string>> IndexExisting(IEnumerable<string> roots, CancellationToken ct)
    {
        var index = new Dictionary<long, List<string>>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in roots)
        {
            if (!System.IO.Directory.Exists(root))
                continue;
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (!index.TryGetValue(file.Length, out var bucket))
                    index[file.Length] = bucket = [];
                bucket.Add(file.FullName);
            }
        }
        return index;
    }

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
