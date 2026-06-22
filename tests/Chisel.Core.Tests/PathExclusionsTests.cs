using System.Runtime.InteropServices;
using FluentAssertions;
using Bennewitz.Ninja.Chisel.Collection;

namespace Bennewitz.Ninja.Chisel.Tests;

/// <summary>
/// Unit tests for the path-exclusion matcher. Pure string/path logic (no filesystem, no MSBuild),
/// so paths are synthesized under the temp root to stay platform-native and rooted.
/// </summary>
public sealed class PathExclusionsTests
{
    private static string TempPath(params string[] parts) =>
        Path.Combine(new[] { Path.GetTempPath(), "chisel-excl" }.Concat(parts).ToArray());

    [Fact]
    public void Empty_ExcludesNothing()
    {
        var exclusions = new PathExclusions(Array.Empty<string>());

        exclusions.IsEmpty.Should().BeTrue();
        exclusions.Count.Should().Be(0);
        exclusions.IsExcluded(TempPath("anything", "X.cs")).Should().BeFalse();
    }

    [Fact]
    public void IsExcluded_FileDeeplyNestedUnderRoot_IsExcluded()
    {
        var root = TempPath("Generated");
        var exclusions = new PathExclusions([root]);

        exclusions.IsExcluded(Path.Combine(root, "a", "b", "c", "File.cs")).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_TheDirectoryItself_IsExcluded()
    {
        var root = TempPath("Dir");
        var exclusions = new PathExclusions([root]);

        exclusions.IsExcluded(root).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_FileNotUnderRoot_IsNotExcluded()
    {
        var exclusions = new PathExclusions([TempPath("Vendor")]);

        exclusions.IsExcluded(TempPath("Source", "Service.cs")).Should().BeFalse();
    }

    [Fact]
    public void IsExcluded_SiblingWithSharedPrefix_IsNotExcluded()
    {
        // The classic prefix bug: excluding "foo" must NOT match the sibling directory "foobar".
        var exclusions = new PathExclusions([TempPath("foo")]);

        exclusions.IsExcluded(TempPath("foobar", "X.cs")).Should().BeFalse(
            "`foo` must not match the sibling directory `foobar`");
    }

    [Fact]
    public void IsExcluded_MultipleRoots_ExcludedIfUnderAny()
    {
        var a = TempPath("A");
        var b = TempPath("B");
        var exclusions = new PathExclusions([a, b]);

        exclusions.IsExcluded(Path.Combine(b, "deep", "X.cs")).Should().BeTrue("under the second root");
        exclusions.IsExcluded(TempPath("C", "X.cs")).Should().BeFalse("under no configured root");
    }

    [Fact]
    public void Constructor_NormalizesTrailingSeparator()
    {
        var baseDir = TempPath("Vendor");
        var exclusions = new PathExclusions([baseDir + Path.DirectorySeparatorChar]);

        exclusions.Roots.Should().ContainSingle().Which.Should().Be(baseDir, "trailing separators are trimmed");
        exclusions.IsExcluded(Path.Combine(baseDir, "a.cs")).Should().BeTrue();
    }

    [Fact]
    public void Constructor_DeduplicatesRoots()
    {
        var dir = TempPath("Dup");
        var exclusions = new PathExclusions([dir, dir + Path.DirectorySeparatorChar]);

        exclusions.Count.Should().Be(1, "the same root supplied twice (with/without trailing sep) collapses to one");
    }

    [Fact]
    public void Constructor_IgnoresEmptyAndWhitespaceEntries()
    {
        var exclusions = new PathExclusions(["", "   "]);

        exclusions.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Constructor_ResolvesRelativeRoots_AgainstCurrentDirectory()
    {
        var exclusions = new PathExclusions([Path.Combine("some", "rel", "dir")]);

        var expected = Path.GetFullPath(Path.Combine("some", "rel", "dir"));
        exclusions.Roots.Should().ContainSingle().Which.Should().Be(expected, "relative inputs are resolved to absolute");
        exclusions.IsExcluded(Path.Combine(expected, "Inner", "X.cs")).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_CaseSensitivity_FollowsHostPlatform()
    {
        // Honors PathComparison: case-insensitive on Windows/macOS, case-sensitive on Linux.
        var exclusions = new PathExclusions([TempPath("Lib")]);
        var differentCase = TempPath("LIB", "File.cs");

        var expected = !RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        exclusions.IsExcluded(differentCase).Should().Be(expected,
            "case sensitivity must match the host filesystem so distinct-case files aren't wrongly dropped/kept");
    }
}
