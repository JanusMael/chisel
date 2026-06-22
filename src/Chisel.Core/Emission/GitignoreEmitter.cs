using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Emission;

/// <summary>
/// Writes a <c>.gitignore</c> into the slice output root so the emitted project behaves like a
/// normal, self-contained repo (build artifacts under <c>bin/</c>/<c>obj/</c> stay untracked).
/// Propagates the analyzed solution's own <c>.gitignore</c> when one is found (walking up from the
/// .sln); otherwise writes a minimal .NET default. Best-effort — failure is reported, not fatal.
/// </summary>
public static class GitignoreEmitter
{
    private const string DefaultGitignore =
        """
        # Build output
        bin/
        obj/

        # IDE
        .vs/
        .idea/
        *.user

        # OS
        .DS_Store
        Thumbs.db
        """;

    /// <summary>Emits <paramref name="outputRoot"/>/.gitignore. Returns the path written, or null on failure.</summary>
    public static string? Emit(string outputRoot, string solutionPath, DiagnosticSink diagnostics)
    {
        var destination = Path.Combine(outputRoot, ".gitignore");

        var ok = diagnostics.Guard("Emit", ".gitignore", () =>
        {
            Directory.CreateDirectory(outputRoot);

            var source = FindNearestGitignore(solutionPath);
            if (source is not null)
            {
                File.Copy(source, destination, overwrite: true);
            }
            else
            {
                File.WriteAllText(destination, DefaultGitignore + Environment.NewLine);
            }
        });

        return ok ? destination : null;
    }

    /// <summary>Walks up from the solution's directory to the filesystem root, returning the first .gitignore found.</summary>
    private static string? FindNearestGitignore(string solutionPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
        string? previous = null;
        // Stop at the filesystem root. Guard on `dir != previous` as well so a path whose
        // GetDirectoryName is a fixed point (e.g. a UNC share root like \\server\share) can't loop.
        while (!string.IsNullOrEmpty(dir) && dir != previous)
        {
            var candidate = Path.Combine(dir, ".gitignore");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            previous = dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
