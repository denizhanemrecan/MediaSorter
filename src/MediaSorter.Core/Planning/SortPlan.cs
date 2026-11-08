using MediaSorter.Core.Analysis;

namespace MediaSorter.Core.Planning;

public enum PlanAction
{
    Copy,
    Move,
    SkipDuplicate,
    SkipUnknown,
    SkipError
}

public enum PlanCategory
{
    Gallery,
    Broken,
    Other,
    None
}

public sealed record MediaInfo(string Path, long Size, FileType Type, ResolvedDate Date, HealthReport Health, string? Error = null);

public sealed record PlanItem(MediaInfo Media, PlanAction Action, PlanCategory Category, string? Destination, string Note)
{
    public bool IsActionable => Action is PlanAction.Copy or PlanAction.Move;
    public string SourcePath => Media.Path;
}

public sealed class SortPlan
{
    public required SortOptions Options { get; init; }
    public required IReadOnlyList<PlanItem> Items { get; init; }

    public int Count(PlanCategory category) => Items.Count(i => i.IsActionable && i.Category == category);
    public int Count(PlanAction action) => Items.Count(i => i.Action == action);
    public long ActionableBytes => Items.Where(i => i.IsActionable).Sum(i => i.Media.Size);
    public IEnumerable<PlanItem> Broken => Items.Where(i => i.Category == PlanCategory.Broken);
}

public readonly record struct ProgressInfo(string Stage, int Done, int Total, string? Current = null)
{
    public double Fraction => Total == 0 ? 0 : (double)Done / Total;
}
