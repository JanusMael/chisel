using System.Diagnostics;
using FluentAssertions;
using Bennewitz.Ninja.Chisel;
using Bennewitz.Ninja.Chisel.Collection;
using Bennewitz.Ninja.Chisel.Workspace;

namespace Bennewitz.Ninja.Chisel.Tests;

[Collection("MSBuild")]
public sealed class SourceGenTests
{
    public SourceGenTests() => MsBuildBootstrapper.EnsureRegistered();

    [Fact]
    public async Task DiscoversGeneratedSymbol_ReferencedOnlyInABody()
    {
        // The walker must reach Generated.GeneratedGreeter, which exists only as generator output
        // and is referenced solely inside Consumer.Run()'s body. Use Materialize so the discovered
        // generated file is retained in the result (Skip/Reference intentionally drop it).
        var outDir = FixturePaths.CreateTempOutputDir(nameof(DiscoversGeneratedSymbol_ReferencedOnlyInABody));
        var result = await SliceRunner.RunAsync(new SliceOptions(
            SolutionPath: FixturePaths.Solution("SourceGen", "SourceGen.sln"),
            TypeName: "SourceGenApp.Consumer",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies,
            ImplementationExpansion: ImplementationExpansion.None,
            SourceGenerators: SourceGeneratorPolicy.Materialize));

        result.Files.Should().Contain(f => f.IsGenerated, "the generated greeter type must be discovered via body analysis");
        result.Files.Should().Contain(f => Path.GetFileName(f.AbsolutePath) == "Consumer.cs");
    }

    [Fact]
    public async Task Materialize_WritesGeneratedCode_AndSliceBuildsStandalone()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Materialize_WritesGeneratedCode_AndSliceBuildsStandalone));
        var result = await SliceRunner.RunAsync(new SliceOptions(
            SolutionPath: FixturePaths.Solution("SourceGen", "SourceGen.sln"),
            TypeName: "SourceGenApp.Consumer",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies,
            ImplementationExpansion: ImplementationExpansion.None,
            SourceGenerators: SourceGeneratorPolicy.Materialize));

        // The generated file is written into a clean _generated/ folder (never under obj/).
        var generatedOnDisk = Directory.GetFiles(result.CopiedSourceRoot, "GeneratedGreeter.g.cs", SearchOption.AllDirectories);
        generatedOnDisk.Should().ContainSingle("the generator output must be materialized to disk");
        generatedOnDisk[0].Replace('\\', '/').Should().Contain("/_generated/", "materialized generator output belongs in _generated/, not obj/");

        // With the generated code vendored, the slice compiles without needing the generator at all.
        var build = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        build.ExitCode.Should().Be(0, $"materialized slice must build standalone. stdout:\n{build.Stdout}\nstderr:\n{build.Stderr}");
    }

    [Fact]
    public async Task Skip_OmitsGeneratedFile_AndWarns()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Skip_OmitsGeneratedFile_AndWarns));
        var result = await SliceRunner.RunAsync(new SliceOptions(
            SolutionPath: FixturePaths.Solution("SourceGen", "SourceGen.sln"),
            TypeName: "SourceGenApp.Consumer",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies,
            ImplementationExpansion: ImplementationExpansion.None,
            SourceGenerators: SourceGeneratorPolicy.Skip));

        result.Files.Should().NotContain(f => f.IsGenerated, "Skip policy must omit generated files");
        result.Warnings.Should().Contain(
            w => w.Contains("source-generated", StringComparison.OrdinalIgnoreCase) && w.Contains("materialize", StringComparison.OrdinalIgnoreCase),
            "the user must be warned that the slice depends on skipped generator output");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunDotnet(string workingDir, string args)
    {
        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }
}
