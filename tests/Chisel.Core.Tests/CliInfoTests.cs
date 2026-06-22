using FluentAssertions;
using Bennewitz.Ninja.Chisel.Cli;
using Bennewitz.Ninja.Chisel.Workspace;

namespace Bennewitz.Ninja.Chisel.Tests;

/// <summary>
/// Pure routing/message checks — no MSBuild, so no <c>[Collection("MSBuild")]</c>. These lock in
/// that --help / --version / no-args are answered by <see cref="CliEntry.TryHandleInfoOnly"/>
/// (which Program calls BEFORE MSBuild registration), so those invocations work with no .NET SDK.
/// </summary>
public sealed class CliInfoTests
{
    [Fact]
    public void NoArgs_IsHandled_WithExit1()
    {
        using var _ = SuppressConsole();
        CliEntry.TryHandleInfoOnly(Array.Empty<string>(), out var exit).Should().BeTrue();
        exit.Should().Be(1);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--version")]
    [InlineData("-V")]
    public void InfoFlags_AreHandled_WithExit0(string flag)
    {
        using var _ = SuppressConsole();
        CliEntry.TryHandleInfoOnly([flag], out var exit).Should().BeTrue();
        exit.Should().Be(0);
    }

    [Fact]
    public void RealInvocation_IsNotHandled()
    {
        CliEntry.TryHandleInfoOnly(
            ["--type", "Foo", "--solution", "x.sln", "--output", "o"], out _)
            .Should().BeFalse();
    }

    [Fact]
    public void NoSdkMessage_IsActionable()
    {
        MsBuildBootstrapper.NoSdkMessage.Should().Contain(".NET SDK");
        MsBuildBootstrapper.NoSdkMessage.Should().Contain("https://dotnet.microsoft.com/download");
    }

    // Briefly silence Console.Out so the usage banner doesn't spam test output. Tests within a
    // class run sequentially, so the captured original is always the real writer.
    private static IDisposable SuppressConsole()
    {
        var original = Console.Out;
        Console.SetOut(TextWriter.Null);
        return new Restore(() => Console.SetOut(original));
    }

    private sealed class Restore(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
