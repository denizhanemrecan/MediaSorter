using System.Globalization;
using System.Text.RegularExpressions;
using MediaSorter.Core.Analysis;

namespace MediaSorter.Core.Planning;

public static partial class LayoutTemplate
{
    public const string Default = "{Year}/{Kind}/{Month}";
    public const string BySource = "{Source}/{Year}/{Kind}/{Month}";
    public const string KindFirst = "{Kind}/{Year}/{Month}";

    public static readonly IReadOnlyList<string> Presets = [Default, BySource, KindFirst, "{Year}/{Month}/{Kind}", "{Year}/{Kind}"];

    public static readonly IReadOnlyList<string> Tokens = ["{Year}", "{Month}", "{MonthNum}", "{MonthName}", "{Day}", "{Kind}", "{Source}", "{Format}"];

    public static string Expand(string template, DateTime date, MediaKind kind, DateSource source, string format)
    {
        var expanded = TokenPattern().Replace(template, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "year" => date.Year.ToString(CultureInfo.InvariantCulture),
            "month" => date.ToString("MM MMMM", CultureInfo.InvariantCulture),
            "monthnum" => date.ToString("MM", CultureInfo.InvariantCulture),
            "monthname" => date.ToString("MMMM", CultureInfo.InvariantCulture),
            "day" => date.ToString("dd", CultureInfo.InvariantCulture),
            "kind" => kind == MediaKind.Video ? "Videos" : "Photos",
            "source" => source == DateSource.FileSystem ? "Modified" : "Taken",
            "format" => format,
            _ => m.Value
        });

        var parts = expanded.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Sanitize)
            .Where(p => p.Length > 0);
        return Path.Combine(parts.ToArray());
    }

    public static string? Validate(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return "Template is empty.";
        foreach (Match m in TokenPattern().Matches(template))
            if (!Tokens.Contains(m.Value, StringComparer.OrdinalIgnoreCase))
                return $"Unknown token {m.Value}.";
        if (Path.IsPathRooted(template) || template.Contains(".."))
            return "Template must be a relative path.";
        return null;
    }

    private static string Sanitize(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(segment.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex TokenPattern();
}
