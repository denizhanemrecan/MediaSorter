using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaSorter.Core.Analysis;
using MediaSorter.Core.Planning;

namespace MediaSorter.App;

public sealed partial class PlanRow(PlanItem item, string targetRoot) : ObservableObject
{
    public PlanItem Item { get; } = item;

    public string FileName => Path.GetFileName(Item.SourcePath);
    public string SourcePath => Item.SourcePath;
    public string Format => Item.Media.Type.Kind == MediaKind.Unknown ? "?" : Item.Media.Type.Format;
    public string Kind => Item.Media.Type.Kind.ToString();
    public string Date => Item.Media.Date.Value == DateTime.MinValue ? "" : Item.Media.Date.Value.ToString("yyyy-MM-dd HH:mm");
    public string DateSource => Item.Media.Date.Source switch
    {
        Core.Analysis.DateSource.Metadata => "metadata",
        Core.Analysis.DateSource.FileName => "file name",
        _ => "modified"
    };
    public string Action => Item.Action switch
    {
        PlanAction.Copy => "copy",
        PlanAction.Move => "move",
        PlanAction.SkipDuplicate => "duplicate",
        PlanAction.SkipUnknown => "skip",
        _ => "error"
    };
    public string Category => Item.Category.ToString();
    public string Destination => Item.Destination is null ? "" : Path.GetRelativePath(targetRoot, Item.Destination);
    public string Note => Item.Note;
    public bool IsBroken => Item.Category == PlanCategory.Broken;
    public bool IsRecoverable => IsBroken && Item.Media.Type.Kind == MediaKind.Video;

    [ObservableProperty]
    private string _recovery = "";
}
