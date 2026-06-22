using System.Security.Cryptography;
using System.Text;
using Bennewitz.Ninja.Chisel.Collection;
using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Emission;

public static class FileCopyEmitter
{
    /// <summary>
    /// Copies each collected file under <paramref name="outputRoot"/>/src/&lt;ProjectName&gt;/...
    /// preserving the path relative to the project directory. Generator-output files (no backing
    /// file on disk) are written from their captured text instead of copied. Returns the mapping
    /// from original absolute path to the new destination path. A file that can't be copied (IO
    /// error, missing, no captured text) is reported and skipped — never fatal.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Copy(
        string outputRoot,
        IEnumerable<CollectedFile> files,
        DiagnosticSink diagnostics,
        CancellationToken cancellationToken = default)
    {
        var srcRoot = Path.Combine(outputRoot, "src");

        // Clean the tool-owned src/ tree so files collected by a PREVIOUS run don't linger. Stale
        // .cs wouldn't be compiled (the csproj lists explicit Compile items) but it pollutes the
        // output and desyncs it from files.json. We remove ONLY src/ — never other content the user
        // may keep under --output. Best-effort: a locked file warns rather than aborting.
        if (Directory.Exists(srcRoot))
        {
            try
            {
                Directory.Delete(srcRoot, recursive: true);
            }
            catch (Exception ex)
            {
                diagnostics.Warn("Copy", $"Could not clean the previous src/ tree ({ex.GetType().Name}); stale files may remain.", srcRoot);
            }
        }

        Directory.CreateDirectory(srcRoot);

        var mapping = new Dictionary<string, string>(PathComparison.Comparer);
        var usedDestinations = new HashSet<string>(PathComparison.Comparer);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dest = ResolveDestination(srcRoot, file, usedDestinations);

            var copied = diagnostics.Guard("Copy", file.AbsolutePath, () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                if (file.IsGenerated)
                {
                    if (file.GeneratedText is null)
                    {
                        diagnostics.Warn("Copy", "Generated file had no captured text; omitted from the slice.", file.AbsolutePath);
                        return;
                    }
                    File.WriteAllText(dest, file.GeneratedText);
                }
                else if (File.Exists(file.AbsolutePath))
                {
                    File.Copy(file.AbsolutePath, dest, overwrite: true);
                }
                else
                {
                    diagnostics.Warn("Copy", "Referenced by a symbol but not present on disk; omitted from the slice.", file.AbsolutePath);
                    return;
                }

                usedDestinations.Add(dest);
                mapping[file.AbsolutePath] = dest;
            });

            _ = copied; // failure already recorded by Guard; continue with the next file.
        }

        return mapping;
    }

    private static string ResolveDestination(string srcRoot, CollectedFile file, HashSet<string> used)
    {
        var projectDir = string.IsNullOrEmpty(file.ProjectFilePath)
            ? null
            : Path.GetDirectoryName(file.ProjectFilePath);

        string relative;
        if (file.IsGenerated)
        {
            // Generated output lives under a synthetic obj path; place it in a clean _generated/
            // folder so the slice never contains an obj/ tree (which the SDK treats specially).
            relative = Path.Combine("_generated", Path.GetFileName(file.AbsolutePath));
        }
        else if (projectDir is not null && file.AbsolutePath.StartsWith(projectDir, PathComparison.Comparison))
        {
            relative = Path.GetRelativePath(projectDir, file.AbsolutePath);
        }
        else
        {
            // File lives outside the project directory (e.g. a <Link> item or a synthetic
            // generated path). Place it under _linked/<hash-of-original-dir>/ so two files that
            // share a base name but come from different directories never clobber each other.
            var originalDir = Path.GetDirectoryName(file.AbsolutePath) ?? "";
            var bucket = ShortHash(originalDir);
            relative = Path.Combine("_linked", bucket, Path.GetFileName(file.AbsolutePath));
        }

        // Files with no owning project (e.g. the synthesized slice-wide global-usings file) live at
        // the src root rather than under a per-project folder.
        var dest = string.IsNullOrWhiteSpace(file.ProjectName)
            ? Path.Combine(srcRoot, relative)
            : Path.Combine(srcRoot, SanitizeSegment(file.ProjectName), relative);

        // Final guard: if two distinct sources still map to the same destination, disambiguate.
        if (used.Contains(dest))
        {
            var dir = Path.GetDirectoryName(dest)!;
            var name = Path.GetFileNameWithoutExtension(dest);
            var ext = Path.GetExtension(dest);
            var suffix = ShortHash(file.AbsolutePath);
            dest = Path.Combine(dir, $"{name}.{suffix}{ext}");
        }

        return dest;
    }

    private static string SanitizeSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }
        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
