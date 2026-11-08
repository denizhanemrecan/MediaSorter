namespace MediaSorter.Core.Planning;

public sealed record SortOptions
{
    public required IReadOnlyList<string> Sources { get; init; }
    public required string TargetRoot { get; init; }
    public string GalleryFolder { get; init; } = "Gallery";
    public string BrokenFolder { get; init; } = "Broken";
    public string OtherFolder { get; init; } = "Other";
    public string Template { get; init; } = LayoutTemplate.Default;
    public bool Move { get; init; }
    public bool FixExtensions { get; init; } = true;
    public bool SkipDuplicates { get; init; } = true;
    public bool IncludeUnknown { get; init; }
    public bool DeepVerifyVideos { get; init; }
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    public string? FfmpegDirectory { get; init; }
}
