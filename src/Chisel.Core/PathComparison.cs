using System.Runtime.InteropServices;

namespace Bennewitz.Ninja.Chisel;

/// <summary>
/// File-path comparison that respects the host OS's case sensitivity: case-insensitive on
/// Windows and macOS (the default APFS/NTFS behavior), case-sensitive on Linux. Using a single
/// blanket <see cref="StringComparer.OrdinalIgnoreCase"/> would collapse <c>Foo.cs</c> and
/// <c>foo.cs</c> — distinct files on Linux — causing one to be silently dropped from the slice.
/// </summary>
public static class PathComparison
{
    public static StringComparer Comparer { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

    public static StringComparison Comparison { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
}
