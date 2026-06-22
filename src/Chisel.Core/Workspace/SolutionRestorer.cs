using System.Diagnostics;
using System.Text;
using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Workspace;

/// <summary>
/// Runs <c>dotnet restore</c> on the solution so the workspace's compilations have their package
/// metadata references available. Best-effort: a restore that fails (or can't be started) is
/// reported as a warning and the run continues — the subsequent workspace load and per-symbol
/// classification will surface any still-missing references on their own.
/// </summary>
public static class SolutionRestorer
{
    public static async Task RestoreAsync(
        string solutionPath,
        DiagnosticSink diagnostics,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionPath))
        {
            // Let WorkspaceLoader raise the (fatal) missing-solution error instead.
            return;
        }

        await diagnostics.GuardAsync("Restore", solutionPath, async () =>
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? Environment.CurrentDirectory,
            };
            // ArgumentList quotes each argument itself — no manual escaping, and no injection risk
            // if the solution path contains spaces or quote characters.
            startInfo.ArgumentList.Add("restore");
            startInfo.ArgumentList.Add(solutionPath);

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            // Throws (caught by the surrounding GuardAsync → non-fatal warning) if 'dotnet' is not
            // on PATH or the process can't be launched.
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            // WaitForExitAsync signals process exit but does not guarantee the redirected output
            // streams are fully drained; the parameterless WaitForExit() does (and returns
            // immediately here, since the process has already exited), so stdout/stderr are
            // complete before we read them.
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var detail = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
                diagnostics.Warn(
                    "Restore",
                    $"'dotnet restore' exited with code {process.ExitCode}; continuing with possibly-incomplete references.{FormatTail(detail)}",
                    solutionPath);
            }
        }).ConfigureAwait(false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string FormatTail(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }
        const int max = 600;
        var tail = trimmed.Length > max ? "…" + trimmed[^max..] : trimmed;
        return " " + tail.Replace(Environment.NewLine, " ").Replace('\n', ' ').Replace('\r', ' ');
    }
}
