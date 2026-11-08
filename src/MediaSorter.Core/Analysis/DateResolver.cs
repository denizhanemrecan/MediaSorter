using System.Globalization;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataDirectory = MetadataExtractor.Directory;

namespace MediaSorter.Core.Analysis;

public enum DateSource
{
    Metadata,
    FileName,
    FileSystem
}

public readonly record struct ResolvedDate(DateTime Value, DateSource Source, string Detail)
{
    public bool IsCaptureDate => Source != DateSource.FileSystem;
}

public static partial class DateResolver
{
    private static readonly DateTime MinPlausible = new(1985, 1, 1);

    private static readonly (string Tag, int Priority)[] TagPriorities =
    [
        ("Date/Time Original", 0),
        ("Date/Time Digitized", 1),
        ("Creation Date", 2),
        ("Created", 3),
        ("Creation Time", 3),
        ("Date/Time", 4)
    ];

    private static readonly string[] UtcDirectories = ["QuickTime Movie Header", "QuickTime Track Header", "MP4", "MP4 Movie Header"];

    private static readonly Dictionary<string, int> MonthNames = BuildMonthTable();

    public static ResolvedDate Resolve(string path, FileType type)
    {
        if (type.Kind != MediaKind.Unknown && TryFromMetadata(path, out var meta, out var detail))
            return new(meta, DateSource.Metadata, detail);
        if (TryFromFileName(Path.GetFileNameWithoutExtension(path), out var named))
            return new(named, DateSource.FileName, "file name");
        var mtime = File.GetLastWriteTime(path);
        return new(mtime, DateSource.FileSystem, "last modified");
    }

    public static bool TryFromMetadata(string path, out DateTime value, out string detail)
    {
        value = default;
        detail = string.Empty;
        IReadOnlyList<MetadataDirectory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(path);
        }
        catch
        {
            return false;
        }

        int bestPriority = int.MaxValue;
        foreach (var dir in directories)
        {
            foreach (var tag in dir.Tags)
            {
                int priority = PriorityOf(tag.Name);
                if (priority >= bestPriority || !dir.TryGetDateTime(tag.Type, out var dt))
                    continue;
                if (UtcDirectories.Contains(dir.Name, StringComparer.OrdinalIgnoreCase))
                    dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
                if (!IsPlausible(dt))
                    continue;
                bestPriority = priority;
                value = dt;
                detail = $"{dir.Name}: {tag.Name}";
            }
        }
        return bestPriority != int.MaxValue;
    }

    public static bool TryFromFileName(string name, out DateTime value)
    {
        value = default;
        var m = NumericDatePattern().Match(name);
        if (m.Success && TryBuild(m.Groups["y"].Value, m.Groups["m"].Value, m.Groups["d"].Value, m.Groups["H"].Value, m.Groups["M"].Value, m.Groups["S"].Value, out value))
            return true;

        m = WordDatePattern().Match(name);
        if (m.Success && MonthNames.TryGetValue(m.Groups["mon"].Value.ToLowerInvariant(), out int month))
            return TryBuild(m.Groups["y"].Value, month.ToString(CultureInfo.InvariantCulture), m.Groups["d"].Value, m.Groups["H"].Value, m.Groups["M"].Value, string.Empty, out value);

        return false;
    }

    private static bool TryBuild(string y, string mo, string d, string h, string mi, string s, out DateTime value)
    {
        value = default;
        try
        {
            value = new DateTime(
                int.Parse(y, CultureInfo.InvariantCulture),
                int.Parse(mo, CultureInfo.InvariantCulture),
                int.Parse(d, CultureInfo.InvariantCulture),
                h.Length > 0 ? int.Parse(h, CultureInfo.InvariantCulture) : 0,
                mi.Length > 0 ? int.Parse(mi, CultureInfo.InvariantCulture) : 0,
                s.Length > 0 ? int.Parse(s, CultureInfo.InvariantCulture) : 0);
            return IsPlausible(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool IsPlausible(DateTime dt) => dt >= MinPlausible && dt <= DateTime.Now.AddDays(1);

    private static int PriorityOf(string tagName)
    {
        foreach (var (tag, priority) in TagPriorities)
            if (tagName.Equals(tag, StringComparison.OrdinalIgnoreCase))
                return priority;
        return int.MaxValue;
    }

    private static Dictionary<string, int> BuildMonthTable()
    {
        var table = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("tr-TR") })
        {
            for (int i = 0; i < 12; i++)
            {
                table.TryAdd(culture.DateTimeFormat.MonthNames[i].ToLowerInvariant(), i + 1);
                table.TryAdd(culture.DateTimeFormat.AbbreviatedMonthNames[i].ToLowerInvariant().TrimEnd('.'), i + 1);
            }
        }
        return table;
    }

    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})[-_.]?(?<m>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])(?:[-_ T.]?(?<H>[01]\d|2[0-3])[-_.:]?(?<M>[0-5]\d)(?:[-_.:]?(?<S>[0-5]\d))?)?(?!\d)")]
    private static partial Regex NumericDatePattern();

    [GeneratedRegex(@"(?<!\d)(?<d>[0-3]?\d)[-_ ](?<mon>\p{L}{3,9})[-_ ](?<y>(?:19|20)\d{2})(?:[-_ ](?<H>[01]?\d|2[0-3])[.:_](?<M>[0-5]\d))?", RegexOptions.IgnoreCase)]
    private static partial Regex WordDatePattern();
}
