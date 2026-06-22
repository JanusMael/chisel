using FluentAssertions;
using Bennewitz.Ninja.Chisel.Workspace;

namespace Bennewitz.Ninja.Chisel.Tests;

public sealed class WorkspaceLoaderTests
{
    [Theory]
    // The real-world message that motivated this: a build-orchestration .proj has no C# to collect.
    [InlineData(@"Cannot open project 'C:\c\allos\PublishEntryPoints.proj' because the file extension '.proj' is not associated with a language.", true)]
    [InlineData(@"Cannot open project 'C:\repo\Native\Engine.vcxproj' because ...", true)]
    [InlineData(@"Cannot open project 'C:\repo\Legacy\Tools.fsproj' because ...", true)]
    [InlineData(@"Cannot open project 'C:\repo\Old\App.vbproj' because ...", true)]
    // A genuine C# project failure must stay fatal.
    [InlineData(@"Cannot open project 'C:\repo\src\App.csproj' because an import was missing.", false)]
    // No project-looking token → not benign (don't mask real errors).
    [InlineData("Some unrelated MSBuild evaluation error with no project path.", false)]
    public void IsNonCSharpProjectFailure_ClassifiesByExtension(string message, bool expected)
    {
        WorkspaceLoader.IsNonCSharpProjectFailure(message).Should().Be(expected);
    }
}
