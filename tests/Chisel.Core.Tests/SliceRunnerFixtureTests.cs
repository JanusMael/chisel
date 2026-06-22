using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Bennewitz.Ninja.Chisel;
using Bennewitz.Ninja.Chisel.Cli;
using Bennewitz.Ninja.Chisel.Workspace;

namespace Bennewitz.Ninja.Chisel.Tests;

[Collection("MSBuild")]
public sealed class SliceRunnerFixtureTests
{
    public SliceRunnerFixtureTests()
    {
        // RegisterDefaults is idempotent via our bootstrapper.
        MsBuildBootstrapper.EnsureRegistered();
    }

    [Fact]
    public async Task Simple_OnInterface_CollectsInterfaceAndConcreteImpls_ExcludesUnrelated()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Simple_OnInterface_CollectsInterfaceAndConcreteImpls_ExcludesUnrelated));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Simple", "Simple.sln"),
            TypeName: "Simple.IFoo",
            OutputDirectory: outDir);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain(["IFoo.cs", "FooA.cs", "FooB.cs"]);
        fileNames.Should().NotContain("Unrelated.cs");

        File.Exists(result.FileListPath).Should().BeTrue();
        File.Exists(result.ReferenceManifestPath).Should().BeTrue();
        File.Exists(result.CsprojPath).Should().BeTrue();
    }

    [Fact]
    public async Task MultiProject_OnInterface_PullsCrossProjectFiles()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(MultiProject_OnInterface_PullsCrossProjectFiles));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("MultiProject", "MultiProject.sln"),
            TypeName: "MultiProject.Abstractions.IService",
            OutputDirectory: outDir);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("IService.cs", "interface itself");
        fileNames.Should().Contain("UserDto.cs", "interface signature references UserDto from another project");
        fileNames.Should().Contain("ServiceImpl.cs", "default polymorphism expansion pulls in the impl");

        var projects = result.Files.Select(f => f.ProjectName).Distinct().ToHashSet();
        projects.Should().Contain(["Abstractions", "Models", "Impl"]);
    }

    [Fact]
    public async Task MethodBody_WithBodiesDepth_PullsTypeReferencedOnlyInsideBody()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(MethodBody_WithBodiesDepth_PullsTypeReferencedOnlyInsideBody));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("MethodBody", "MethodBody.sln"),
            TypeName: "MethodBody.Orchestrator",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies,
            ImplementationExpansion: ImplementationExpansion.None);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("Orchestrator.cs");
        fileNames.Should().Contain("ConcreteHelper.cs", "the bodies walker must find `new ConcreteHelper()` inside the constructor");
        fileNames.Should().NotContain("Untouched.cs");
    }

    [Fact]
    public async Task MethodBody_SignaturesOnly_ExcludesTypesUsedOnlyInsideBodies()
    {
        // The default (signatures-only) walk treats body usages as out of scope: ConcreteHelper is
        // only `new`-ed inside Orchestrator's constructor body, so it must NOT be collected.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(MethodBody_SignaturesOnly_ExcludesTypesUsedOnlyInsideBodies));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("MethodBody", "MethodBody.sln"),
            TypeName: "MethodBody.Orchestrator",
            OutputDirectory: outDir);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("Orchestrator.cs", "the seed type itself is always collected");
        fileNames.Should().NotContain("ConcreteHelper.cs", "signatures-only must not follow method-body usages");
        fileNames.Should().NotContain("Untouched.cs");
    }

    [Fact]
    public async Task MultiProject_GeneratedCsproj_ActuallyBuilds_WithNoWarnings()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(MultiProject_GeneratedCsproj_ActuallyBuilds_WithNoWarnings));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("MultiProject", "MultiProject.sln"),
            TypeName: "MultiProject.Abstractions.IService",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        var buildResult = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        buildResult.ExitCode.Should().Be(0, $"multi-project slice must build with no warnings (catches CS2002 regressions). stdout:\n{buildResult.Stdout}\nstderr:\n{buildResult.Stderr}");
    }

    [Fact]
    public async Task Simple_GeneratedCsproj_ActuallyBuilds()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Simple_GeneratedCsproj_ActuallyBuilds));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Simple", "Simple.sln"),
            TypeName: "Simple.IFoo",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        // Build the generated slice csproj in a separate dotnet process so it doesn't share
        // MSBuild state with the in-proc workspace.
        var buildResult = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        buildResult.ExitCode.Should().Be(0, $"slice csproj must build with no warnings. stdout:\n{buildResult.Stdout}\nstderr:\n{buildResult.Stderr}");
    }

    [Fact]
    public async Task Generics_OnOpenGeneric_PullsConstraintInterface_AndDerivedImpl_AndBuilds()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Generics_OnOpenGeneric_PullsConstraintInterface_AndDerivedImpl_AndBuilds));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Generics", "Generics.sln"),
            // Arity-1 form so TypeResolver maps it to the `Generics.Repository`1` open generic definition.
            TypeName: "Generics.Repository<T>",
            OutputDirectory: outDir,
            // `all` so the non-seed IEntity constraint interface is expanded to its impls; bodies so
            // the slice is self-compilable for the build assertion below.
            WalkDepth: WalkDepth.Bodies,
            ImplementationExpansion: ImplementationExpansion.All);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("Repository.cs", "the seed type itself");
        fileNames.Should().Contain("IEntity.cs", "pulled in via the `where T : IEntity` type-parameter constraint");
        fileNames.Should().Contain("Customer.cs", "with --expand-impls all, the IEntity interface is expanded to its concrete impls");

        var buildResult = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        buildResult.ExitCode.Should().Be(0, $"generics slice must build with no warnings. stdout:\n{buildResult.Stdout}\nstderr:\n{buildResult.Stderr}");
    }

    [Fact]
    public async Task Generics_SeedOnlyExpansion_DoesNotExpandConstraintInterface()
    {
        // Default expansion is seed-only: IEntity is reached via the seed's `where T : IEntity`
        // constraint (so its DECLARATION is collected), but because it is NOT the seed, its concrete
        // implementations (Customer) must NOT be pulled in.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Generics_SeedOnlyExpansion_DoesNotExpandConstraintInterface));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Generics", "Generics.sln"),
            TypeName: "Generics.Repository<T>",
            OutputDirectory: outDir);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("Repository.cs", "the seed type itself");
        fileNames.Should().Contain("IEntity.cs", "the constraint interface's declaration is still collected");
        fileNames.Should().NotContain("Customer.cs", "seed-only expansion must not expand a non-seed interface to its implementations");
    }

    [Fact]
    public async Task Partial_OnPartialClass_CollectsAllDeclaringFiles_AndBuilds()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Partial_OnPartialClass_CollectsAllDeclaringFiles_AndBuilds));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Partial", "Partial.sln"),
            TypeName: "Partial.Foo",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain(
            ["Foo.Constructors.cs", "Foo.Properties.cs", "Foo.Methods.cs"],
            "a partial type's DeclaringSyntaxReferences span every file that declares it");

        var buildResult = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        buildResult.ExitCode.Should().Be(0, $"partial slice must build with no warnings. stdout:\n{buildResult.Stdout}\nstderr:\n{buildResult.Stderr}");
    }

    [Fact]
    public async Task ExternalPackage_RecordsPackage_DoesNotCollectPackageSource()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(ExternalPackage_RecordsPackage_DoesNotCollectPackageSource));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("ExternalPackage", "ExternalPackage.sln"),
            TypeName: "ExternalPackage.MyClass",
            OutputDirectory: outDir);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("MyClass.cs", "the seed type's own source is collected");
        result.Files.Should().NotContain(
            f => Path.GetFileName(f.AbsolutePath).StartsWith("Newtonsoft", StringComparison.OrdinalIgnoreCase),
            "out-of-codebase NuGet symbols are leaves — their source is referenced, never vendored");

        // references.json must record { id: Newtonsoft.Json, version: 13.0.3 }.
        // NuGet lowercases package-folder ids on disk, so the recorded id may be "newtonsoft.json";
        // compare case-insensitively since NuGet package ids are case-insensitive.
        using var refsDoc = JsonDocument.Parse(File.ReadAllText(result.ReferenceManifestPath));
        var packages = refsDoc.RootElement.GetProperty("packages").EnumerateArray().ToList();
        packages.Should().Contain(
            p => string.Equals(p.GetProperty("id").GetString(), "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase)
              && p.GetProperty("version").GetString() == "13.0.3",
            "references.json must list the Newtonsoft.Json package and its version");

        // Slice.csproj must carry the PackageReference so the slice can restore the package.
        var csproj = XDocument.Load(result.CsprojPath);
        csproj.Descendants("PackageReference").Should().Contain(
            e => string.Equals((string?)e.Attribute("Include"), "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase)
              && (string?)e.Attribute("Version") == "13.0.3",
            "the generated csproj must reference Newtonsoft.Json 13.0.3");
    }

    [Fact]
    public async Task ImplicitUsings_PropagatesImplicitUsings_AndBuilds()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(ImplicitUsings_PropagatesImplicitUsings_AndBuilds));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("ImplicitUsings", "ImplicitUsings.sln"),
            TypeName: "ImplicitUsings.IdGenerator",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("IdGenerator.cs");

        // The source compiles only because <ImplicitUsings>enable</ImplicitUsings> supplies `using System;`.
        // The slice csproj must re-enable it, or the bare Guid/DateTime references won't bind.
        var csproj = XDocument.Load(result.CsprojPath);
        var implicitUsings = csproj.Descendants("ImplicitUsings").FirstOrDefault()?.Value;
        implicitUsings.Should().Be("enable", "the source project enables implicit usings, so the slice must too");

        var buildResult = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        buildResult.ExitCode.Should().Be(0, $"implicit-usings slice must build with no warnings (proves the implicit `using System;` is preserved). stdout:\n{buildResult.Stdout}\nstderr:\n{buildResult.Stderr}");
    }

    [Fact]
    public async Task MultiTarget_ResolvesWithoutAmbiguity_AndBuilds()
    {
        // Regression: a multi-targeted project surfaces the same logical type once per TFM as
        // distinct symbols. Before the fix, resolution reported a spurious ambiguity and threw.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(MultiTarget_ResolvesWithoutAmbiguity_AndBuilds));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("MultiTarget", "MultiTarget.sln"),
            TypeName: "MultiTarget.Widget",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("Widget.cs");
        result.Warnings.Should().Contain(w => w.Contains("multi-targets"), "the user should be told a TFM was auto-chosen");

        var buildResult = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        buildResult.ExitCode.Should().Be(0, $"multi-target slice must build. stdout:\n{buildResult.Stdout}\nstderr:\n{buildResult.Stderr}");
    }

    [Fact]
    public async Task MultiTarget_WithPreferredTfm_SelectsThatFramework()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(MultiTarget_WithPreferredTfm_SelectsThatFramework));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("MultiTarget", "MultiTarget.sln"),
            TypeName: "MultiTarget.Widget",
            OutputDirectory: outDir,
            PreferredTargetFramework: "net8.0");

        var result = await SliceRunner.RunAsync(options);

        result.Files.Should().OnlyContain(f => f.TargetFramework == "net8.0", "--tfm net8.0 must pin the slice to net8.0");
        result.Warnings.Should().NotContain(w => w.Contains("multi-targets"), "an explicit --tfm match should not warn about auto-selection");
    }

    [Fact]
    public async Task NestedType_OnInner_PullsEnclosingTypeAndItsBase_AndBuilds()
    {
        // Regression (MEDIUM-5): slicing on a nested type must pull in the enclosing type's full
        // declaration graph — including a base class declared in a different file.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(NestedType_OnInner_PullsEnclosingTypeAndItsBase_AndBuilds));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("NestedType", "NestedType.sln"),
            TypeName: "NestedType.Outer.Inner",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("Outer.cs", "the enclosing type's declaration is required");
        fileNames.Should().Contain("OuterBase.cs", "the enclosing type's base class (separate file) must be pulled in");
        fileNames.Should().NotContain("Unrelated.cs");

        var buildResult = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        buildResult.ExitCode.Should().Be(0, $"nested-type slice must build. stdout:\n{buildResult.Stdout}\nstderr:\n{buildResult.Stderr}");
    }

    [Fact]
    public async Task Shapes_OnInterface_CollectsImplsAcrossProjects_FlattensAndBuilds()
    {
        // The headline integration test: seed on an interface and collect a slice whose files are
        // spread across FOUR projects — the interface (Contracts), shared models (Geometry), and
        // concrete implementations living in two separate projects (Primitives + Composite). The
        // flattened slice (no ProjectReferences) must compile.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Shapes_OnInterface_CollectsImplsAcrossProjects_FlattensAndBuilds));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Shapes", "Shapes.sln"),
            TypeName: "Contracts.IShape",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain(
            ["IShape.cs", "Point.cs", "Size.cs", "Circle.cs", "Rectangle.cs", "Group.cs"],
            "the interface, its model dependencies, and every concrete implementation must be collected");
        fileNames.Should().NotContain("Triangle.cs", "a type that does not implement IShape and is referenced by nothing must be excluded");

        // The collected files genuinely span multiple projects.
        var projects = result.Files.Select(f => f.ProjectName).Distinct().ToHashSet();
        projects.Should().BeEquivalentTo(["Contracts", "Geometry", "Primitives", "Composite"],
            "implementations and models are spread across four distinct projects");

        // Each contributing project provides at least one file (true cross-project collection).
        result.Files.Select(f => f.ProjectName).Distinct().Should().HaveCountGreaterThanOrEqualTo(4);

        // The slice is flattened — it must NOT carry ProjectReferences; everything is a Compile item.
        var csproj = XDocument.Load(result.CsprojPath);
        csproj.Descendants("ProjectReference").Should().BeEmpty("a slice collapses cross-project sources into one flat project");
        csproj.Descendants("Compile").Should().HaveCount(result.Files.Count, "every collected file is an explicit Compile item");

        // The decisive check: the cross-project slice compiles standalone.
        var build = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        build.ExitCode.Should().Be(0, $"the cross-project slice must compile. stdout:\n{build.Stdout}\nstderr:\n{build.Stderr}");
    }

    [Fact]
    public async Task GlobalUsings_AuthoredGlobalUsingFile_IsCollected_AndSliceBuilds()
    {
        // Regression: a dedicated `global using` file declares no types, so the type-graph walk
        // never reaches it. The seed `ReportBuilder` uses StringBuilder with no local using and
        // ImplicitUsings disabled — so the slice only compiles if the authored global-usings file
        // is pulled in. Pre-fix this failed with CS0246.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(GlobalUsings_AuthoredGlobalUsingFile_IsCollected_AndSliceBuilds));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("GlobalUsings", "GlobalUsings.sln"),
            TypeName: "GlobalUsings.ReportBuilder",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("ReportBuilder.cs", "the seed type itself");
        result.Files.Should().Contain(
            f => f.IsGenerated && f.GeneratedText != null && f.GeneratedText.Contains("global using System.Text;"),
            "the authored global using must be harvested into the synthesized global-usings file");

        var build = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        build.ExitCode.Should().Be(0, $"the slice must compile with the authored global usings present. stdout:\n{build.Stdout}\nstderr:\n{build.Stderr}");
    }

    [Fact]
    public async Task MixedSettings_HoistsStrictestSettings_WarnsOnDisagreement_AndBuilds()
    {
        // The seed project (App) has the WEAKER settings (ImplicitUsings/Nullable disabled) and
        // sorts first, while its dependency (Lib) is stricter. The slice must hoist the highest
        // settings (not the seed-project baseline) or Lib's files — which rely on implicit usings —
        // won't compile.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(MixedSettings_HoistsStrictestSettings_WarnsOnDisagreement_AndBuilds));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("MixedSettings", "MixedSettings.sln"),
            TypeName: "App.Invoice",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain(["Invoice.cs", "Money.cs"]);

        // The csproj hoists the strictest/highest values across both projects.
        var csproj = XDocument.Load(result.CsprojPath);
        csproj.Descendants("ImplicitUsings").Single().Value.Should().Be("enable", "ImplicitUsings is hoisted via Any(), not All()");
        csproj.Descendants("Nullable").Single().Value.Should().Be("enable", "Nullable is hoisted to the strictest setting");

        // The user is warned that the contributing projects disagreed.
        result.Warnings.Should().Contain(w => w.Contains("Nullable") && w.Contains("disagree"));
        result.Warnings.Should().Contain(w => w.Contains("ImplicitUsings") && w.Contains("disagree"));

        var build = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        build.ExitCode.Should().Be(0, $"the slice must compile with hoisted settings. stdout:\n{build.Stdout}\nstderr:\n{build.Stderr}");
    }

    [Fact]
    public async Task Attributes_TypeofArguments_AreDiscovered_AndSliceBuilds()
    {
        // Payload appears only as a typeof() constructor arg; ExtraA/ExtraB only inside a typeof()
        // array in a named arg. These must be pulled in via attribute-argument walking.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Attributes_TypeofArguments_AreDiscovered_AndSliceBuilds));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Attributes", "Attributes.sln"),
            TypeName: "Attributes.Widget",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain(
            ["Widget.cs", "MarkerAttribute.cs", "Payload.cs", "ExtraA.cs", "ExtraB.cs"],
            "the attribute class and every typeof() argument type (ctor arg + named-arg array) must be collected");
        fileNames.Should().NotContain("Unused.cs");

        var build = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        build.ExitCode.Should().Be(0, $"the attributes slice must compile. stdout:\n{build.Stdout}\nstderr:\n{build.Stderr}");
    }

    [Fact]
    public async Task NoDerived_OnInterface_DoesNotPullImplementations()
    {
        // With ExpandDerived disabled, an interface seed must NOT pull in its implementations —
        // proving the polymorphic-expansion gate actually works (prior tests only used --no-derived
        // where no derivations existed to expand).
        var outDir = FixturePaths.CreateTempOutputDir(nameof(NoDerived_OnInterface_DoesNotPullImplementations));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Simple", "Simple.sln"),
            TypeName: "Simple.IFoo",
            OutputDirectory: outDir,
            ImplementationExpansion: ImplementationExpansion.None);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("IFoo.cs", "the interface itself is always collected");
        fileNames.Should().NotContain("FooA.cs", "derived expansion is disabled");
        fileNames.Should().NotContain("FooB.cs", "derived expansion is disabled");
    }

    [Fact]
    public async Task CleanRun_ExposesDiagnostics_WithNoErrors_AndStreamsThemLive()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(CleanRun_ExposesDiagnostics_WithNoErrors_AndStreamsThemLive));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Simple", "Simple.sln"),
            TypeName: "Simple.IFoo",
            OutputDirectory: outDir);

        var streamed = new List<Bennewitz.Ninja.Chisel.Diagnostics.SliceDiagnostic>();
        var result = await SliceRunner.RunAsync(options, streamed.Add);

        // A clean slice produces no error-severity diagnostics, and Diagnostics is the structured
        // superset of Warnings.
        result.Diagnostics.Should().NotContain(d => d.Severity == Bennewitz.Ninja.Chisel.Diagnostics.DiagnosticSeverity.Error);
        result.Warnings.Should().BeEquivalentTo(
            result.Diagnostics
                .Where(d => d.Severity == Bennewitz.Ninja.Chisel.Diagnostics.DiagnosticSeverity.Warning)
                .Select(d => d.Message));

        // Everything reported in the result was also streamed live during execution.
        streamed.Should().BeEquivalentTo(result.Diagnostics);
    }

    [Fact]
    public async Task Slice_EmitsGitignore_PropagatedFromSolutionTree()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Slice_EmitsGitignore_PropagatedFromSolutionTree));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Simple", "Simple.sln"),
            TypeName: "Simple.IFoo",
            OutputDirectory: outDir);

        var result = await SliceRunner.RunAsync(options);

        result.GitignorePath.Should().NotBeNull();
        File.Exists(result.GitignorePath!).Should().BeTrue();
        Path.GetFileName(result.GitignorePath!).Should().Be(".gitignore");

        // The repo's own .gitignore (the dotnet template, with the "[Bb]in/" glob) lives above the
        // fixture solution; finding it proves we propagated rather than wrote the lowercase default.
        var content = await File.ReadAllTextAsync(result.GitignorePath!);
        content.Should().Contain("[Bb]in/", "the analyzed solution's .gitignore should be propagated into the slice");
    }

    [Fact]
    public async Task Restore_RunsDotnetRestore_NonFatally_AndStillSlices()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Restore_RunsDotnetRestore_NonFatally_AndStillSlices));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Simple", "Simple.sln"),
            TypeName: "Simple.IFoo",
            OutputDirectory: outDir,
            Restore: true);

        var result = await SliceRunner.RunAsync(options);

        // The slice is produced as usual, and restoring a healthy solution records no error.
        result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).Should().Contain("IFoo.cs");
        result.Diagnostics.Should().NotContain(
            d => d.Stage == "Restore" && d.Severity == Bennewitz.Ninja.Chisel.Diagnostics.DiagnosticSeverity.Error,
            "restoring a valid solution must not produce an error");
    }

    [Fact]
    public async Task Progress_EmitsPhases_AndPerTypeActivity()
    {
        // Phases are coarse milestones; Activity updates name the type currently being processed so
        // a CLI heartbeat can show "what is it looking at" during long phases.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Progress_EmitsPhases_AndPerTypeActivity));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Shapes", "Shapes.sln"),
            TypeName: "Contracts.IShape",
            OutputDirectory: outDir);

        var progress = new List<Bennewitz.Ninja.Chisel.SliceProgress>();
        await SliceRunner.RunAsync(options, onDiagnostic: null, onProgress: progress.Add);

        progress.Should().Contain(p => p.Kind == Bennewitz.Ninja.Chisel.ProgressKind.Phase && p.Message.Contains("Walking"),
            "phase milestones are reported");
        progress.Should().Contain(p => p.Kind == Bennewitz.Ninja.Chisel.ProgressKind.Activity && p.Message.Contains("walking "),
            "each dequeued type is reported as fine-grained activity");
        progress.Should().Contain(p => p.Kind == Bennewitz.Ninja.Chisel.ProgressKind.Activity && p.Message.Contains("finding implementations of"),
            "the expensive implementation scan names the type it is expanding");
    }

    [Fact]
    public async Task Attributes_OnEveryTarget_ArePulledIn()
    {
        // Custom attributes applied to the class, a property, a field, a method, a parameter, an
        // enum type, and an enum value must all be collected (signatures-only default — attributes
        // are part of the declared surface).
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Attributes_OnEveryTarget_ArePulledIn));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("AttrTargets", "AttrTargets.sln"),
            TypeName: "AttrTargets.Subject",
            OutputDirectory: outDir);

        var result = await SliceRunner.RunAsync(options);

        var symbols = result.Files.SelectMany(f => f.ContainingSymbols).ToHashSet();
        symbols.Should().Contain(
            [
                "global::AttrTargets.OnClassAttribute",
                "global::AttrTargets.OnPropertyAttribute",
                "global::AttrTargets.OnFieldAttribute",
                "global::AttrTargets.OnMethodAttribute",
                "global::AttrTargets.OnParameterAttribute",
                "global::AttrTargets.OnEnumAttribute",       // attribute on the enum TYPE
                "global::AttrTargets.OnEnumValueAttribute",  // attribute on an enum VALUE
                "global::AttrTargets.Priority",              // enum reached ONLY as an attribute argument value
            ],
            "attributes on every target are part of the declared surface and must be collected");
    }

    [Fact]
    public async Task SignaturesOnly_StillRecordsPackages_UsedOnlyInMethodBodies()
    {
        // Collected files are copied whole (bodies included), so packages a body needs must end up
        // in the slice csproj even though signatures-only collection doesn't COLLECT body types.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(SignaturesOnly_StillRecordsPackages_UsedOnlyInMethodBodies));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("BodyPackage", "BodyPackage.sln"),
            TypeName: "BodyPackage.Service",
            OutputDirectory: outDir);
        // Default WalkDepth is Signatures.

        var result = await SliceRunner.RunAsync(options);

        result.ExternalReferences.Should().Contain(
            r => string.Equals(r.PackageId, "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase) && r.PackageVersion == "13.0.3",
            "a NuGet type used only inside a collected file's body must still be recorded as a package reference");

        var csproj = XDocument.Load(result.CsprojPath);
        csproj.Descendants("PackageReference").Should().Contain(
            e => string.Equals((string?)e.Attribute("Include"), "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase),
            "the slice csproj must carry the package the copied body needs");
    }

    [Fact]
    public async Task Rerun_CleansStaleFilesFromPreviousSlice()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Rerun_CleansStaleFilesFromPreviousSlice));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Simple", "Simple.sln"),
            TypeName: "Simple.IFoo",
            OutputDirectory: outDir);

        await SliceRunner.RunAsync(options);

        // Plant a stale file under the tool-owned src/ tree (as a previous run with a different
        // result would leave behind), plus an unrelated user file directly under --output.
        var srcRoot = Path.Combine(outDir, "src");
        var stale = Path.Combine(srcRoot, "Ghost", "Stale.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "// left over from a previous run");
        var userFile = Path.Combine(outDir, "NOTES.md");
        File.WriteAllText(userFile, "keep me");

        await SliceRunner.RunAsync(options);

        File.Exists(stale).Should().BeFalse("the src/ tree is cleaned on each run so stale files don't linger");
        Directory.GetFiles(srcRoot, "IFoo.cs", SearchOption.AllDirectories).Should().NotBeEmpty("the real slice is still emitted");
        File.Exists(userFile).Should().BeTrue("only src/ is cleaned — other content under --output is left alone");
    }

    [Fact]
    public async Task AttributeTypeOf_RecoveredFromSyntax_WhenSymbolArgsThrow()
    {
        // [Marker(typeof(Needed), null)] uses a params ctor; the bare null trips Roslyn's
        // AttributeData.ConstructorArguments NRE (same shape as [DataRow(null)]). The symbol-based
        // attribute read therefore can't see typeof(Needed) — it must be recovered from syntax, even
        // in the default signatures mode (no body walk).
        var outDir = FixturePaths.CreateTempOutputDir(nameof(AttributeTypeOf_RecoveredFromSyntax_WhenSymbolArgsThrow));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("AttrTypeOf", "AttrTypeOf.sln"),
            TypeName: "AttrTypeOf.Annotated",
            OutputDirectory: outDir);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("Annotated.cs");
        fileNames.Should().Contain("MarkerAttribute.cs", "the attribute class is part of the declared surface");
        fileNames.Should().Contain("Needed.cs", "typeof(Needed) in the attribute must be recovered from syntax even when the symbol-based arg read NREs");
        fileNames.Should().NotContain("Unrelated.cs");
    }

    [Fact]
    public async Task AttrTypeOf_RecoversTypeofInAttribute_EvenWhenSymbolArgReadNREs()
    {
        // [Marker(typeof(Needed), null)] — the params + bare-null shape trips Roslyn's
        // AttributeData.ConstructorArguments NRE (same as MSTest's [DataRow(null)]). The symbol
        // path can't read the args, but the syntax-recovery pass must still collect Needed.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(AttrTypeOf_RecoversTypeofInAttribute_EvenWhenSymbolArgReadNREs));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("AttrTypeOf", "AttrTypeOf.sln"),
            TypeName: "AttrTypeOf.Annotated",
            OutputDirectory: outDir);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("Annotated.cs");
        fileNames.Should().Contain("MarkerAttribute.cs", "the attribute class is a declared dependency");
        fileNames.Should().Contain("Needed.cs", "typeof(Needed) in the attribute is recovered from syntax even though the symbol-based arg read NREs");
        fileNames.Should().NotContain("Unrelated.cs");

        // Prove the symbol-based arg read actually failed — otherwise this wouldn't be exercising
        // the syntax-recovery path that closes the hole.
        result.Diagnostics.Should().Contain(
            d => d.Stage == "Walk" && d.Message.Contains("MarkerAttribute") && d.Message.Contains("NullReferenceException"),
            "the params+null attribute shape trips the Roslyn ConstructorArguments NRE, so recovery comes from syntax");
    }

    [Fact]
    public async Task GlobalUsings_FromMixedFile_AreHarvested_WithoutCollectingItsType_AndSliceBuilds()
    {
        // CORRECT-7: a `global using` lives in Bootstrap.cs, which ALSO declares a type the seed
        // never reaches. The directive must still be harvested (so StringBuilder binds) without
        // over-collecting Bootstrap itself.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(GlobalUsings_FromMixedFile_AreHarvested_WithoutCollectingItsType_AndSliceBuilds));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("GlobalUsingsMixed", "GlobalUsingsMixed.sln"),
            TypeName: "GlobalUsingsMixed.Report",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("Report.cs", "the seed type");
        fileNames.Should().NotContain("Bootstrap.cs", "the mixed file's unrelated type must not be over-collected");
        result.Files.Should().Contain(
            f => f.IsGenerated && f.GeneratedText != null && f.GeneratedText.Contains("global using System.Text;"),
            "the global using authored in the mixed file must be harvested into the synthesized file");

        var build = RunDotnet(outDir, $"build \"{result.CsprojPath}\" -v:minimal -warnaserror");
        build.ExitCode.Should().Be(0, $"the slice must compile via the harvested global using. stdout:\n{build.Stdout}\nstderr:\n{build.Stderr}");
    }

    [Fact]
    public void Cli_FormatJson_WritesParseableManifestToStdout_AndResultJson()
    {
        // End-to-end: the built CLI must emit a clean JSON manifest on STDOUT under --format json
        // (progress goes to stderr), and always write <output>/result.json.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Cli_FormatJson_WritesParseableManifestToStdout_AndResultJson));
        var sln = FixturePaths.Solution("Simple", "Simple.sln");

        // The CLI's own build output (with its runtimeconfig/deps) sits next to the test bin under
        // src/Chisel.Cli instead of tests/Chisel.Core.Tests.
        var chiselDll = Path.Combine(
            AppContext.BaseDirectory.Replace(
                Path.Combine("tests", "Chisel.Core.Tests"),
                Path.Combine("src", "Chisel.Cli")),
            "chisel.dll");
        File.Exists(chiselDll).Should().BeTrue($"expected the CLI build output at {chiselDll}");

        var run = RunDotnet(outDir, $"\"{chiselDll}\" -t Simple.IFoo -s \"{sln}\" -o \"{outDir}\" --format json");
        run.ExitCode.Should().Be(0, $"stderr:\n{run.Stderr}");

        using var doc = JsonDocument.Parse(run.Stdout); // stdout must be pure JSON
        doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("counts").GetProperty("files").GetInt32().Should().BeGreaterThan(0);

        File.Exists(Path.Combine(outDir, "result.json")).Should().BeTrue("result.json is always written");
    }

    [Fact]
    public async Task Exclude_DropsFilesUnderExcludedDirectory_AndLogsThem()
    {
        // Shapes collects across four project dirs (Contracts/Geometry/Primitives/Composite).
        // Excluding the Composite/ subtree must drop Group.cs (its only file) — removing Composite
        // as a contributor — while keeping everything else, and logging each drop as a diagnostic.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Exclude_DropsFilesUnderExcludedDirectory_AndLogsThem));
        var compositeDir = Path.Combine(FixturePaths.FixturesRoot, "Shapes", "Composite");
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Shapes", "Shapes.sln"),
            TypeName: "Contracts.IShape",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies,
            ExcludePaths: [compositeDir]);

        var streamed = new List<Bennewitz.Ninja.Chisel.Diagnostics.SliceDiagnostic>();
        var result = await SliceRunner.RunAsync(options, streamed.Add);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("IShape.cs", "files outside the excluded subtree are still collected");
        fileNames.Should().NotContain("Group.cs", "Group.cs lives under the excluded Composite/ directory");

        result.Files.Select(f => f.ProjectName).Should().NotContain(
            "Composite", "a project whose only collected file was excluded stops contributing");

        // The drop is logged: a warning-severity "Exclude" diagnostic naming the dropped file...
        result.Diagnostics.Should().Contain(
            d => d.Stage == "Exclude"
              && d.Severity == Bennewitz.Ninja.Chisel.Diagnostics.DiagnosticSeverity.Warning
              && d.Item != null && d.Item.EndsWith("Group.cs"),
            "each excluded file is reported as a warning diagnostic carrying its path");
        // ...and it was streamed live like every other diagnostic.
        streamed.Should().Contain(d => d.Stage == "Exclude", "exclusions stream live during the run");
    }

    [Fact]
    public async Task Exclude_NotConfigured_CollectsEverything_AndLogsNoExclusions()
    {
        var outDir = FixturePaths.CreateTempOutputDir(nameof(Exclude_NotConfigured_CollectsEverything_AndLogsNoExclusions));
        var options = new SliceOptions(
            SolutionPath: FixturePaths.Solution("Shapes", "Shapes.sln"),
            TypeName: "Contracts.IShape",
            OutputDirectory: outDir,
            WalkDepth: WalkDepth.Bodies);

        var result = await SliceRunner.RunAsync(options);

        var fileNames = result.Files.Select(f => Path.GetFileName(f.AbsolutePath)).ToHashSet();
        fileNames.Should().Contain("Group.cs", "with no --exclude, the Composite file is collected as before");
        result.Diagnostics.Should().NotContain(d => d.Stage == "Exclude", "no exclusions configured → no Exclude diagnostics");
    }

    [Fact]
    public async Task CliEntry_LiterallyQuotedPaths_AreTolerated_AndDoNotCrash()
    {
        // Regression: Rider's multi-line "Program arguments" editor passes each line verbatim, so
        // paths arrive wrapped in literal quotes. Before the fix, Path.GetFullPath turned "<outDir>"
        // into a garbage relative path and the unguarded Directory.CreateDirectory threw before any
        // output. The quotes must be stripped so a clean run succeeds.
        var outDir = FixturePaths.CreateTempOutputDir(nameof(CliEntry_LiterallyQuotedPaths_AreTolerated_AndDoNotCrash));
        var sln = FixturePaths.Solution("Simple", "Simple.sln");

        var code = await CliEntry.RunAsync([
            "--type", "\"Simple.IFoo\"",
            "--solution", $"\"{sln}\"",
            "--output", $"\"{outDir}\"",
        ]);

        code.Should().Be(0, "literally-quoted paths must be tolerated (quotes stripped), not crash before output");
        File.Exists(Path.Combine(outDir, "result.json")).Should().BeTrue("the run completed and wrote its manifest");
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
        // Read both streams concurrently — sequential ReadToEnd can deadlock if a verbose build
        // fills the stderr pipe while we're blocked draining stdout.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }
}

[CollectionDefinition("MSBuild", DisableParallelization = true)]
public sealed class MSBuildCollection { }
