using System.Text.Json;
using Bennewitz.Ninja.Chisel;
using Bennewitz.Ninja.Chisel.Cli;
using Bennewitz.Ninja.Chisel.Collection;
using Bennewitz.Ninja.Chisel.Diagnostics;
using FluentAssertions;

namespace Bennewitz.Ninja.Chisel.Tests;

public sealed class CliManifestTests
{
    private static SliceOptions Options() => new(
        SolutionPath: @"C:\repo\App.sln",
        TypeName: "MyNS.IFoo",
        OutputDirectory: @"C:\out",
        WalkDepth: WalkDepth.Signatures,
        ImplementationExpansion: ImplementationExpansion.SeedOnly,
        SourceGenerators: SourceGeneratorPolicy.Reference);

    private static SliceResult Result(IReadOnlyList<SliceDiagnostic>? diagnostics = null) => new(
        SeedTypeDisplay: "global::MyNS.IFoo",
        SeedFilePath: @"C:\repo\IFoo.cs",
        InSourceTypeCount: 7,
        Files: new[]
        {
            new CollectedFile(@"C:\repo\IFoo.cs", "App", @"C:\repo\App.csproj", "net10.0", [], false),
            new CollectedFile(@"C:\repo\Bar.cs", "App", @"C:\repo\App.csproj", "net10.0", [], false),
        },
        ExternalReferences: new[]
        {
            new ExternalReference("Newtonsoft.Json", "13.0.0.0", "Newtonsoft.Json", "13.0.3", @"C:\.nuget\packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.dll"),
            new ExternalReference("System.Runtime", "10.0.0.0", null, null, @"C:\packs\Microsoft.NETCore.App.Ref\10.0.0\ref\net10.0\System.Runtime.dll"),
        },
        Projects: Array.Empty<CollectedProject>(),
        Warnings: Array.Empty<string>(),
        Diagnostics: diagnostics ?? Array.Empty<SliceDiagnostic>(),
        FileListPath: @"C:\out\files.json",
        ReferenceManifestPath: @"C:\out\references.json",
        CsprojPath: @"C:\out\Slice.csproj",
        CopiedSourceRoot: @"C:\out\src",
        GitignorePath: @"C:\out\.gitignore");

    [Fact]
    public void Create_ProducesStableCamelCaseManifest()
    {
        var manifest = RunManifest.Create(Options(), Result(), TimeSpan.FromSeconds(3.5), exitCode: 0, version: "9.9.9", logPath: @"C:\out\chisel.log");

        using var doc = JsonDocument.Parse(manifest.ToJson());
        var root = doc.RootElement;

        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("tool").GetProperty("name").GetString().Should().Be("chisel");
        root.GetProperty("tool").GetProperty("version").GetString().Should().Be("9.9.9");
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        // Mode mirrors the CLI flag spellings.
        var mode = root.GetProperty("mode");
        mode.GetProperty("walkDepth").GetString().Should().Be("signatures");
        mode.GetProperty("expansion").GetString().Should().Be("seed");
        mode.GetProperty("sourceGenerators").GetString().Should().Be("reference");

        var counts = root.GetProperty("counts");
        counts.GetProperty("inSourceTypes").GetInt32().Should().Be(7);
        counts.GetProperty("files").GetInt32().Should().Be(2);
        counts.GetProperty("externalReferences").GetInt32().Should().Be(2);
        counts.GetProperty("packages").GetInt32().Should().Be(1);

        var packages = root.GetProperty("packages").EnumerateArray().ToList();
        packages.Should().ContainSingle();
        packages[0].GetProperty("id").GetString().Should().Be("Newtonsoft.Json");
        packages[0].GetProperty("version").GetString().Should().Be("13.0.3");

        root.GetProperty("seed").GetProperty("displayName").GetString().Should().Be("global::MyNS.IFoo");
    }

    [Fact]
    public void Create_MapsDiagnostics()
    {
        var diags = new[]
        {
            new SliceDiagnostic(DiagnosticSeverity.Warning, "Walk", "a warning", "Foo.cs"),
            new SliceDiagnostic(DiagnosticSeverity.Error, "Body", "an error", null),
        };

        var manifest = RunManifest.Create(Options(), Result(diags), TimeSpan.Zero, 0, "1.0.0", @"C:\out\chisel.log");
        using var doc = JsonDocument.Parse(manifest.ToJson());

        var items = doc.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items[0].GetProperty("severity").GetString().Should().Be("Warning");
        items[0].GetProperty("stage").GetString().Should().Be("Walk");
        items[1].GetProperty("severity").GetString().Should().Be("Error");
        // A null item must be omitted (WhenWritingNull), not serialized as null.
        items[1].TryGetProperty("item", out _).Should().BeFalse();
    }

    [Fact]
    public void CreateFailure_MarksUnsuccessful_WithError()
    {
        var manifest = RunManifest.CreateFailure(Options(), "1.0.0", exitCode: 3, logPath: @"C:\out\chisel.log",
            errorKind: "TypeResolution", errorMessage: "not found");

        using var doc = JsonDocument.Parse(manifest.ToJson());
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("exitCode").GetInt32().Should().Be(3);
        root.GetProperty("error").GetProperty("kind").GetString().Should().Be("TypeResolution");
        root.GetProperty("error").GetProperty("message").GetString().Should().Be("not found");
    }

    [Fact]
    public void ResolveExitCode_StrictWithError_Is6_OtherwiseZero()
    {
        var withError = Result(new[] { new SliceDiagnostic(DiagnosticSeverity.Error, "Walk", "boom", null) });
        var warnOnly = Result(new[] { new SliceDiagnostic(DiagnosticSeverity.Warning, "Walk", "meh", null) });

        CliEntry.ResolveExitCode(withError, strict: true).Should().Be(6);
        CliEntry.ResolveExitCode(withError, strict: false).Should().Be(0, "default stays best-effort");
        CliEntry.ResolveExitCode(warnOnly, strict: true).Should().Be(0, "warnings don't trip --strict");
    }
}
