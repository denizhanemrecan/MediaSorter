using System.Globalization;
using System.Text;

namespace MediaSorter.Core.Planning;

public sealed record ExecutionResult(int Succeeded, int Failed, IReadOnlyList<string> Errors, string JournalPath);

public static class Executor
{
    private const string CopyOp = "copy";
    private const string MoveOp = "move";

    public static async Task<ExecutionResult> ExecuteAsync(SortPlan plan, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var actionable = plan.Items.Where(i => i.IsActionable).ToList();
        var stateDir = Path.Combine(Path.GetFullPath(plan.Options.TargetRoot), Planner.StateFolder);
        Directory.CreateDirectory(stateDir);
        var journalPath = Path.Combine(stateDir, $"journal-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.tsv");

        int ok = 0;
        var errors = new List<string>();
        await using var journal = new StreamWriter(journalPath, append: false, Encoding.UTF8);

        for (int i = 0; i < actionable.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = actionable[i];
            progress?.Report(new ProgressInfo(item.Action == PlanAction.Move ? "Moving" : "Copying", i, actionable.Count, item.SourcePath));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                if (item.Action == PlanAction.Move)
                {
                    await Task.Run(() => File.Move(item.SourcePath, item.Destination!, overwrite: false), ct).ConfigureAwait(false);
                }
                else
                {
                    await CopyAsync(item.SourcePath, item.Destination!, ct).ConfigureAwait(false);
                }
                await journal.WriteLineAsync($"{(item.Action == PlanAction.Move ? MoveOp : CopyOp)}\t{item.SourcePath}\t{item.Destination}").ConfigureAwait(false);
                await journal.FlushAsync(ct).ConfigureAwait(false);
                ok++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{item.SourcePath}: {ex.Message}");
            }
        }

        progress?.Report(new ProgressInfo("Done", actionable.Count, actionable.Count));
        return new ExecutionResult(ok, errors.Count, errors, journalPath);
    }

    public static async Task<ExecutionResult> UndoAsync(string journalPath, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var targetRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(journalPath)))!;
        var lines = (await File.ReadAllLinesAsync(journalPath, ct).ConfigureAwait(false)).Reverse().ToList();
        int ok = 0;
        var errors = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var parts = lines[i].Split('\t');
            if (parts.Length != 3)
                continue;
            var (op, src, dst) = (parts[0], parts[1], parts[2]);
            progress?.Report(new ProgressInfo("Undoing", i, lines.Count, dst));
            try
            {
                if (op == MoveOp)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(src)!);
                    File.Move(dst, src, overwrite: false);
                }
                else if (File.Exists(dst))
                {
                    File.Delete(dst);
                }
                DeleteEmptyParents(Path.GetDirectoryName(dst)!, targetRoot);
                ok++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{dst}: {ex.Message}");
            }
        }
        if (errors.Count == 0)
            File.Move(journalPath, journalPath + ".undone", overwrite: true);
        return new ExecutionResult(ok, errors.Count, errors, journalPath);
    }

    public static string? LatestJournal(string targetRoot)
    {
        var dir = Path.Combine(Path.GetFullPath(targetRoot), Planner.StateFolder);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "journal-*.tsv").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
            : null;
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        const int bufferSize = 1 << 20;
        bool created = false;
        try
        {
            await using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dst = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous))
            {
                created = true;
                await src.CopyToAsync(dst, bufferSize, ct).ConfigureAwait(false);
            }
            File.SetCreationTime(destination, File.GetCreationTime(source));
            File.SetLastWriteTime(destination, File.GetLastWriteTime(source));
        }
        catch
        {
            if (created)
                TryDelete(destination);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void DeleteEmptyParents(string dir, string stopAt)
    {
        try
        {
            while (dir.Length > stopAt.Length
                   && dir.StartsWith(stopAt, StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(dir)
                   && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir)!;
            }
        }
        catch (IOException) { }
    }
}
