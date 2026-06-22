namespace Bennewitz.Ninja.Chisel.Collection;

/// <summary>
/// A set of directory subtrees to exclude from file collection. A file is excluded when it lies
/// within any excluded directory (recursively) — used to carve out vendored/generated/out-of-scope
/// regions the caller does not want vendored into the slice. Matching is full-path based and honors
/// the host OS's path case sensitivity (<see cref="PathComparison"/>), and is robust against the
/// classic prefix bug (<c>/a/foo</c> must not match <c>/a/foobar/x.cs</c>).
///
/// Implementation detail of the collection pipeline — callers embedding <c>Chisel.Core</c> configure
/// exclusions through <see cref="SliceOptions.ExcludePaths"/> (a list of strings), not by constructing
/// this directly. A malformed exclusion path is skipped (best-effort), consistent with the rest of the
/// pipeline; the CLI validates user input up front via <see cref="Path.GetFullPath(string)"/>.
/// </summary>
internal sealed class PathExclusions
{
    // Normalized to full paths with any trailing directory separator removed, deduplicated.
    private readonly IReadOnlyList<string> _roots;

    public PathExclusions(IEnumerable<string> directories)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(PathComparison.Comparer);
        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            string normalized;
            try
            {
                // Idempotent for already-absolute paths; resolves relative inputs against the CWD.
                normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed exclusion path can't match anything on disk — skip it rather than
                // aborting the whole collection. (The CLI validates user input up front via
                // Path.GetFullPath, so this only guards direct API misuse.)
                continue;
            }

            if (seen.Add(normalized))
            {
                roots.Add(normalized);
            }
        }

        _roots = roots;
    }

    /// <summary>The normalized, absolute exclusion roots (trailing separators removed).</summary>
    public IReadOnlyList<string> Roots => _roots;

    public int Count => _roots.Count;

    public bool IsEmpty => _roots.Count == 0;

    /// <summary>True when <paramref name="filePath"/> is the excluded directory itself or lies within it.</summary>
    public bool IsExcluded(string filePath)
    {
        if (_roots.Count == 0 || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var root in _roots)
        {
            // Exact match (the path IS the excluded directory), or a descendant: compare against
            // "root + separator" so "/a/foo" does not spuriously match "/a/foobar/x.cs".
            if (full.Equals(root, PathComparison.Comparison))
            {
                return true;
            }

            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, PathComparison.Comparison))
            {
                return true;
            }
        }

        return false;
    }
}
