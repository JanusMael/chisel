using System.Xml.Linq;
using FluentAssertions;
using Bennewitz.Ninja.Chisel.Collection;
using Bennewitz.Ninja.Chisel.Emission;

namespace Bennewitz.Ninja.Chisel.Tests;

public sealed class CsprojGeneratorTests
{
    private static CollectedProject Project(
        string name,
        IReadOnlyList<string> defineConstants,
        bool implicitUsings = true,
        string nullable = "Enable",
        string sdk = "Microsoft.NET.Sdk",
        string? rootNamespace = null) =>
        new(
            Name: name,
            FilePath: $@"C:\repo\{name}\{name}.csproj",
            TargetFramework: "net10.0",
            Sdk: sdk,
            LangVersion: "13.0",
            Nullable: nullable,
            AllowUnsafeBlocks: false,
            ImplicitUsings: implicitUsings,
            RootNamespace: rootNamespace ?? name,
            DefineConstants: defineConstants);

    private static XDocument WriteAndLoad(
        IReadOnlyList<CollectedProject> projects,
        IEnumerable<ExternalReference> references)
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(CsprojGeneratorTests));
        CsprojGenerator.Write(outDir, new Dictionary<string, string>(), projects, references);
        return XDocument.Load(Path.Combine(outDir, "Slice.csproj"));
    }

    [Fact]
    public void DefineConstants_StripsSdkInjectedSymbols_KeepsUserDefined()
    {
        var projects = new[]
        {
            Project("A", ["DEBUG", "TRACE", "NET10_0", "NET10_0_OR_GREATER", "NETCOREAPP", "FEATURE_X"]),
        };

        var doc = WriteAndLoad(projects, []);

        var define = doc.Descendants("DefineConstants").FirstOrDefault()?.Value;
        define.Should().Be("FEATURE_X", "SDK-injected DEBUG/TRACE/framework monikers must be dropped, leaving only user constants");
    }

    [Fact]
    public void DefineConstants_AllSdkInjected_OmitsElementEntirely()
    {
        var projects = new[]
        {
            Project("A", ["DEBUG", "TRACE", "NET10_0_OR_GREATER"]),
        };

        var doc = WriteAndLoad(projects, []);

        doc.Descendants("DefineConstants").Should().BeEmpty("with no user constants, no DefineConstants element should be emitted");
    }

    [Fact]
    public void RootNamespace_IsEmitted_WhenSet()
    {
        var doc = WriteAndLoad([Project("A", [], rootNamespace: "My.Root.Namespace")], []);
        doc.Descendants("RootNamespace").FirstOrDefault()?.Value
            .Should().Be("My.Root.Namespace", "a non-default RootNamespace must be carried into the slice csproj");
    }

    [Fact]
    public void Disagreement_OnSdkAndRootNamespace_IsWarned()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(CsprojGeneratorTests));
        var projects = new[]
        {
            Project("A", [], sdk: "Microsoft.NET.Sdk", rootNamespace: "A"),
            Project("B", [], sdk: "Microsoft.NET.Sdk.Web", rootNamespace: "B"),
        };

        var warnings = CsprojGenerator.Write(outDir, new Dictionary<string, string>(), projects, []);

        warnings.Should().Contain(w => w.Contains("Sdk") && w.Contains("disagree"), "a Web vs non-Web SDK split is significant");
        warnings.Should().Contain(w => w.Contains("RootNamespace") && w.Contains("disagree"));
    }

    [Fact]
    public void PackageReference_PicksHighestSemanticVersion_NotLexicographic()
    {
        // "10.0.0" must beat "9.0.0"; string ordering would wrongly pick "9.0.0".
        var refs = new[]
        {
            new ExternalReference("Pkg", "9.0.0.0", "Pkg", "9.0.0", @"C:\.nuget\packages\pkg\9.0.0\lib\net10.0\pkg.dll"),
            new ExternalReference("Pkg", "10.0.0.0", "Pkg", "10.0.0", @"C:\.nuget\packages\pkg\10.0.0\lib\net10.0\pkg.dll"),
        };

        var doc = WriteAndLoad([Project("A", [])], refs);

        var pkg = doc.Descendants("PackageReference").Single();
        ((string?)pkg.Attribute("Version")).Should().Be("10.0.0");
    }
}
