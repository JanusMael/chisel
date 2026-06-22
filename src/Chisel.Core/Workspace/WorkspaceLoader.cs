using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Bennewitz.Ninja.Chisel.Workspace;

public sealed record WorkspaceDiagnostic(WorkspaceDiagnosticKind Kind, string Message);

public sealed class WorkspaceLoadException : Exception
{
    public IReadOnlyList<WorkspaceDiagnostic> Diagnostics { get; }

    public WorkspaceLoadException(string message, IReadOnlyList<WorkspaceDiagnostic> diagnostics)
        : base(message)
    {
        Diagnostics = diagnostics;
    }
}

public sealed class LoadedWorkspace : IDisposable
{
    public MSBuildWorkspace Workspace { get; }
    public Solution Solution { get; }
    public IReadOnlyList<WorkspaceDiagnostic> Diagnostics { get; }

    internal LoadedWorkspace(MSBuildWorkspace workspace, Solution solution, IReadOnlyList<WorkspaceDiagnostic> diagnostics)
    {
        Workspace = workspace;
        Solution = solution;
        Diagnostics = diagnostics;
    }

    public void Dispose() => Workspace.Dispose();
}

public static class WorkspaceLoader
{
    public static async Task<LoadedWorkspace> OpenSolutionAsync(
        string solutionPath,
        bool allowPartial,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException($"Solution file not found: {solutionPath}", solutionPath);
        }

        MsBuildBootstrapper.EnsureRegistered();

        var workspace = MSBuildWorkspace.Create();
        var diagnostics = new List<WorkspaceDiagnostic>();
        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            lock (diagnostics)
            {
                diagnostics.Add(new WorkspaceDiagnostic(args.Diagnostic.Kind, args.Diagnostic.Message));
            }
        });

        try
        {
            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // The failure handler may run on a thread-pool thread; snapshot under the same lock
            // it writes under before reading.
            List<WorkspaceDiagnostic> snapshot;
            lock (diagnostics)
            {
                snapshot = diagnostics.ToList();
            }

            if (!allowPartial)
            {
                // A failure to open a NON-C# project (.proj, .vcxproj, .fsproj, .vbproj, ...) is
                // benign: those hold no C# for us to collect, and MSBuildWorkspace skips them while
                // still loading the C# projects. They remain in the diagnostics (surfaced as
                // warnings) but must not abort the run. Only genuine .csproj/load failures are fatal.
                var fatalFailures = snapshot
                    .Where(d => d.Kind == WorkspaceDiagnosticKind.Failure && !IsNonCSharpProjectFailure(d.Message))
                    .ToList();

                if (fatalFailures.Count > 0)
                {
                    var summary = string.Join(Environment.NewLine, fatalFailures.Select(f => "  " + f.Message));
                    workspace.Dispose();
                    throw new WorkspaceLoadException(
                        $"Solution loaded with {fatalFailures.Count} failure(s); pass --allow-partial to continue anyway:{Environment.NewLine}{summary}",
                        snapshot);
                }
            }

            return new LoadedWorkspace(workspace, solution, snapshot);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static readonly Regex QuotedToken = new(@"'([^']+)'", RegexOptions.Compiled);

    /// <summary>
    /// True if a workspace failure message refers to a project file that is NOT a C# project
    /// (e.g. <c>.proj</c>, <c>.vcxproj</c>, <c>.fsproj</c>, <c>.vbproj</c>). Such projects hold no
    /// C# source to collect, so failing to open them is not fatal. Matches the first quoted token
    /// that looks like a project path and checks its extension; a quoted <c>.csproj</c> (a real
    /// failure) returns false, as does any message without a project-looking token.
    /// </summary>
    internal static bool IsNonCSharpProjectFailure(string message)
    {
        foreach (Match m in QuotedToken.Matches(message))
        {
            var ext = Path.GetExtension(m.Groups[1].Value);
            if (ext.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            {
                return !ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }
}
