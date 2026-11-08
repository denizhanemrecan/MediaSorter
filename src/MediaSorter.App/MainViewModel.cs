using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaSorter.Core.Planning;
using MediaSorter.Core.Recovery;
using MediaSorter.Core.Tools;
using Microsoft.Win32;

namespace MediaSorter.App;

public sealed partial class MainViewModel : ObservableObject
{
    public static readonly string[] Filters = ["All", "Gallery", "Broken", "Duplicates", "Skipped"];

    private CancellationTokenSource? _cts;
    private SortPlan? _plan;

    public MainViewModel()
    {
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => o is PlanRow row && Matches(row);
        Ffmpeg = FfmpegTools.Locate();
        Log(Ffmpeg is null
            ? "ffmpeg/ffprobe not found: deep verify and recovery are disabled. Install ffmpeg (winget install Gyan.FFmpeg) and restart."
            : $"ffmpeg: {Ffmpeg.Ffmpeg}");
    }

    public FfmpegTools? Ffmpeg { get; }
    public ObservableCollection<string> Sources { get; } = [];
    public ObservableCollection<PlanRow> Rows { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public ICollectionView RowsView { get; }
    public IReadOnlyList<string> TemplatePresets => LayoutTemplate.Presets;
    public string TemplateHelp => "Tokens: " + string.Join(" ", LayoutTemplate.Tokens);

    [ObservableProperty] private string? _selectedSource;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))] private string _targetRoot = "";
    [ObservableProperty] private string _template = LayoutTemplate.Default;
    [ObservableProperty] private bool _move;
    [ObservableProperty] private bool _fixExtensions = true;
    [ObservableProperty] private bool _skipDuplicates = true;
    [ObservableProperty] private bool _includeUnknown;
    [ObservableProperty] private bool _deepVerify;
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private int _recoveryRotation;

    public static readonly int[] Rotations = [0, 90, 180, 270];
    [ObservableProperty] private string _status = "Add source folders and pick a target.";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _progressIndeterminate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(ExecuteCommand), nameof(UndoCommand), nameof(RecoverBrokenCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _planReady;

    partial void OnFilterChanged(string value) => RowsView.Refresh();

    partial void OnTemplateChanged(string value) => InvalidatePlan();
    partial void OnMoveChanged(bool value) => InvalidatePlan();
    partial void OnFixExtensionsChanged(bool value) => InvalidatePlan();
    partial void OnSkipDuplicatesChanged(bool value) => InvalidatePlan();
    partial void OnIncludeUnknownChanged(bool value) => InvalidatePlan();
    partial void OnTargetRootChanged(string value) => InvalidatePlan();

    [RelayCommand]
    private void AddSource()
    {
        var dialog = new OpenFolderDialog { Multiselect = true, Title = "Select source folders" };
        if (dialog.ShowDialog() != true)
            return;
        foreach (var folder in dialog.FolderNames.Where(f => !Sources.Contains(f, StringComparer.OrdinalIgnoreCase)))
            Sources.Add(folder);
        InvalidatePlan();
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveSource()
    {
        if (SelectedSource is not null)
            Sources.Remove(SelectedSource);
        InvalidatePlan();
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        var dialog = new OpenFolderDialog { Title = "Select target folder" };
        if (dialog.ShowDialog() == true)
            TargetRoot = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenTarget()
    {
        if (Directory.Exists(TargetRoot))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TargetRoot}\"") { UseShellExecute = true });
    }

    private bool CanAnalyze() => !IsBusy && Sources.Count > 0 && !string.IsNullOrWhiteSpace(TargetRoot);

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task Analyze() => RunBusy("Analyzing", async ct =>
    {
        if (LayoutTemplate.Validate(Template) is { } error)
        {
            Log(error);
            return;
        }
        var options = BuildOptions();
        var plan = await Task.Run(() => new Planner().BuildAsync(options, ProgressReporter(), ct), ct);

        Rows.Clear();
        foreach (var item in plan.Items)
            Rows.Add(new PlanRow(item, options.TargetRoot));
        _plan = plan;
        PlanReady = plan.Items.Any(i => i.IsActionable);

        Summary = $"{plan.Count(PlanCategory.Gallery)} to gallery  ·  {plan.Count(PlanCategory.Broken)} broken  ·  " +
                  $"{plan.Count(PlanAction.SkipDuplicate)} duplicates  ·  {plan.Count(PlanAction.SkipUnknown)} skipped  ·  " +
                  $"{plan.ActionableBytes / 1024.0 / 1024.0:N0} MB";
        Log($"Plan: {Summary}");
        Status = "Review the plan, then press " + (Move ? "Move" : "Copy") + ".";
    });

    private bool CanExecute() => !IsBusy && PlanReady;

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private Task Execute() => RunBusy(Move ? "Moving" : "Copying", async ct =>
    {
        var result = await Task.Run(() => Executor.ExecuteAsync(_plan!, ProgressReporter(), ct), ct);
        foreach (var error in result.Errors)
            Log(error);
        Log($"Done: {result.Succeeded} ok, {result.Failed} failed. Undo journal: {result.JournalPath}");
        Status = $"Done: {result.Succeeded} files {(Move ? "moved" : "copied")}.";
        PlanReady = false;
    });

    private bool CanUndo() => !IsBusy && !string.IsNullOrWhiteSpace(TargetRoot);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task Undo() => RunBusy("Undoing", async ct =>
    {
        var journal = Executor.LatestJournal(TargetRoot);
        if (journal is null)
        {
            Log("Nothing to undo in this target.");
            return;
        }
        var result = await Task.Run(() => Executor.UndoAsync(journal, ProgressReporter(), ct), ct);
        foreach (var error in result.Errors)
            Log(error);
        Log($"Undo: {result.Succeeded} reverted, {result.Failed} failed ({Path.GetFileName(journal)}).");
        InvalidatePlan();
    });

    private bool CanRecover() => !IsBusy && Ffmpeg is not null && Rows.Any(r => r.IsRecoverable);

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private Task RecoverBroken() => RunBusy("Recovering", async ct =>
    {
        var recovery = new VideoRecovery(Ffmpeg!);
        var outDir = Path.Combine(TargetRoot, Planner.RecoveredFolder);
        var targets = Rows.Where(r => r.IsRecoverable).ToList();
        for (int i = 0; i < targets.Count; i++)
        {
            var row = targets[i];
            var input = File.Exists(row.Item.Destination) ? row.Item.Destination! : row.SourcePath;
            var output = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + "_recovered.mp4");
            Status = $"Recovering {i + 1}/{targets.Count}: {row.FileName}";
            ProgressValue = (double)i / targets.Count;
            row.Recovery = "working...";
            Log($"--- {row.FileName}");
            var progress = new Progress<string>(Log);
            try
            {
                var result = await Task.Run(() => recovery.RecoverAsync(input, output, new RecoveryOptions { RotateClockwise = RecoveryRotation }, progress, ct), ct);
                row.Recovery = result.Success ? "recovered" : "failed: " + result.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.Recovery = "failed: " + ex.Message;
                Log(ex.Message);
            }
        }
        Status = $"Recovery finished. Output: {outDir}";
    });

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private SortOptions BuildOptions() => new()
    {
        Sources = Sources.ToList(),
        TargetRoot = TargetRoot,
        Template = Template,
        Move = Move,
        FixExtensions = FixExtensions,
        SkipDuplicates = SkipDuplicates,
        IncludeUnknown = IncludeUnknown,
        DeepVerifyVideos = DeepVerify && Ffmpeg is not null,
        FfmpegDirectory = Ffmpeg is null ? null : Path.GetDirectoryName(Ffmpeg.Ffmpeg)
    };

    private async Task RunBusy(string stage, Func<CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressIndeterminate = true;
        Status = stage + "...";
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
            Log($"{stage} cancelled.");
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            Log(ex.ToString());
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = false;
            ProgressValue = 0;
            _cts.Dispose();
            _cts = null;
            RecoverBrokenCommand.NotifyCanExecuteChanged();
        }
    }

    private IProgress<ProgressInfo> ProgressReporter() => new Progress<ProgressInfo>(p =>
    {
        ProgressIndeterminate = p.Total == 0;
        ProgressValue = p.Fraction;
        Status = p.Total > 0 ? $"{p.Stage} {p.Done:N0} / {p.Total:N0}" : p.Stage + "...";
    });

    private void InvalidatePlan()
    {
        if (_plan is null)
            return;
        _plan = null;
        PlanReady = false;
        Status = "Settings changed: analyze again.";
    }

    private bool Matches(PlanRow row) => Filter switch
    {
        "Gallery" => row.Item.IsActionable && row.Item.Category == PlanCategory.Gallery,
        "Broken" => row.IsBroken,
        "Duplicates" => row.Item.Action == PlanAction.SkipDuplicate,
        "Skipped" => row.Item.Action is PlanAction.SkipUnknown or PlanAction.SkipError,
        _ => true
    };

    private void Log(string message)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (LogLines.Count > 2000)
            LogLines.RemoveAt(0);
    }
}
