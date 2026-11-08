using System.Diagnostics;
using System.Text;

namespace MediaSorter.Core.Tools;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, byte[]? StdOutBytes = null);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, byte[]? stdin, CancellationToken ct, bool binaryStdout = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!binaryStdout)
            psi.StandardOutputEncoding = Encoding.UTF8;
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");
        var stdoutBytes = binaryStdout ? new MemoryStream() : null;
        Task<string> stdoutTask = binaryStdout
            ? process.StandardOutput.BaseStream.CopyToAsync(stdoutBytes!, ct).ContinueWith(_ => string.Empty, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default)
            : process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            if (stdin is not null)
            {
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(stdin, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // ffmpeg may close stdin early once it has seen enough data.
                }
                finally
                {
                    process.StandardInput.Close();
                }
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout, stderr, stdoutBytes?.ToArray());
    }
}
