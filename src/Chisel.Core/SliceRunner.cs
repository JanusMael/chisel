using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Bennewitz.Ninja.Chisel.Collection;
using Bennewitz.Ninja.Chisel.Diagnostics;
using Bennewitz.Ninja.Chisel.Emission;
using Bennewitz.Ninja.Chisel.Resolution;
using Bennewitz.Ninja.Chisel.Walking;
using Bennewitz.Ninja.Chisel.Workspace;

namespace Bennewitz.Ninja.Chisel;

public sealed record SliceOptions(
    string SolutionPath,
    string TypeName,
    string OutputDirectory,
    string? ProjectFilter = null,
    string? PreferredTargetFramework = null,
    WalkDepth WalkDepth = WalkDepth.Signatures,
    ImplementationExpansion ImplementationExpansion = ImplementationExpansion.SeedOnly,
    SourceGeneratorPolicy SourceGenerators = SourceGeneratorPolicy.Reference,
    bool AllowPartial = false,
    bool Restore = false,
    // Directory subtrees whose files are dropped from the slice (each is reported as an "Exclude"
    // diagnostic). Null/empty means no exclusions. See <see cref="PathExclusions"/>.
    IReadOnlyList<string>? ExcludePaths = null);

public sealed record SliceResult(
    string SeedTypeDisplay,
    string? SeedFilePath,
    int InSourceTypeCount,
    IReadOnlyList<CollectedFile> Files,
    IReadOnlyList<ExternalReference> ExternalReferences,
    IReadOnlyList<CollectedProject> Projects,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SliceDiagnostic> Diagnostics,
    string FileListPath,
    string ReferenceManifestPath,
    string CsprojPath,
    string CopiedSourceRoot,
    string? GitignorePath);

public static partial class SliceRunner
{
    // Synthetic source path for the harvested global-usings file. Never a real on-disk path — the
    // file is written from GeneratedText; only its file name (GlobalUsings.cs) is used for the dest.
    private const string SynthesizedGlobalUsingsMarker = "<synthesized>/GlobalUsings.cs";

    public static Task<SliceResult> RunAsync(SliceOptions options, CancellationToken cancellationToken = default)
        => RunAsync(options, onDiagnostic: null, onProgress: null, cancellationToken);

    /// <summary>
    /// Builds a slice. Per-item failures (a file that won't bind, a reference that won't resolve,
    /// a file that can't be copied) are reported as non-fatal <see cref="SliceDiagnostic"/>s and
    /// streamed to <paramref name="onDiagnostic"/> as they happen — the slice is still produced.
    /// Only truly fatal conditions throw: a missing solution (<see cref="FileNotFoundException"/>),
    /// an unloadable workspace (<see cref="WorkspaceLoadException"/>, unless <c>AllowPartial</c>),
    /// and an unresolvable/ambiguous seed type (<see cref="TypeResolutionException"/>).
    /// </summary>
    public static async Task<SliceResult> RunAsync(
        SliceOptions options,
        Action<SliceDiagnostic>? onDiagnostic,
        Action<SliceProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.OutputDirectory);

        var diagnostics = new DiagnosticSink(onDiagnostic);
        void Phase(string message) => onProgress?.Invoke(new SliceProgress(ProgressKind.Phase, message));
        void Activity(string message) => onProgress?.Invoke(new SliceProgress(ProgressKind.Activity, message));

        // Optionally restore first so package metadata references are present when the workspace
        // builds compilations. Best-effort — a failed restore warns and continues.
        if (options.Restore)
        {
            Phase("Restoring NuGet packages (dotnet restore)…");
            await SolutionRestorer.RestoreAsync(options.SolutionPath, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        Phase($"Loading solution '{Path.GetFileName(options.SolutionPath)}' (large solutions can take a while)…");
        using var loaded = await WorkspaceLoader.OpenSolutionAsync(options.SolutionPath, options.AllowPartial, cancellationToken)
            .ConfigureAwait(false);

        // Surface any workspace-load diagnostics that were tolerated under --allow-partial.
        foreach (var wd in loaded.Diagnostics)
        {
            if (wd.Kind == WorkspaceDiagnosticKind.Failure)
            {
                diagnostics.Warn("Workspace", wd.Message);
            }
        }

        var solution = ApplyTargetFrameworkFilter(loaded.Solution, options.PreferredTargetFramework, diagnostics);
        var csharpProjectCount = solution.Projects.Count(p => p.Language == LanguageNames.CSharp);

        Phase($"Analyzing {csharpProjectCount} C# project(s) (compiling each — typically the slowest step)…");
        var assemblyIndex = await AssemblyIndex.BuildAsync(solution, diagnostics, cancellationToken).ConfigureAwait(false);

        Phase($"Resolving seed type '{options.TypeName}'…");
        var seed = await TypeResolver.ResolveAsync(
            solution, options.TypeName, options.ProjectFilter, assemblyIndex, cancellationToken).ConfigureAwait(false);

        Phase($"Walking the dependency graph from {seed.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} ({options.WalkDepth.ToString().ToLowerInvariant()} depth)…");
        var expansionRoots = BuildExpansionRoots(seed);
        var walker = new DependencyWalker(solution, assemblyIndex, options.WalkDepth, options.ImplementationExpansion, expansionRoots, diagnostics);
        var walk = await walker.WalkAsync(seed, Activity, cancellationToken).ConfigureAwait(false);

        Phase($"Collecting source files ({walk.InSourceTypes.Count} in-source type(s), {walk.ExternalTypes.Count} external)…");
        var collectedSourceFiles = await SourceFileCollector.CollectAsync(
            solution, walk.InSourceTypes, options.SourceGenerators, diagnostics, cancellationToken).ConfigureAwait(false);

        var exclusions = new PathExclusions(options.ExcludePaths ?? Array.Empty<string>());
        if (!exclusions.IsEmpty)
        {
            Phase($"Applying {exclusions.Count} exclusion path(s)…");
            collectedSourceFiles = ApplyExclusions(collectedSourceFiles, exclusions, diagnostics);
        }

        var contributingProjects = collectedSourceFiles.Select(f => f.Project).Distinct().ToList();
        var projects = await ProjectFileCollector.CollectAsync(contributingProjects, diagnostics, cancellationToken).ConfigureAwait(false);
        var projectByPath = projects.ToDictionary(p => p.FilePath, PathComparison.Comparer);

        var files = collectedSourceFiles.Select(f =>
        {
            var meta = f.Project.FilePath is not null && projectByPath.TryGetValue(f.Project.FilePath, out var cp) ? cp : null;
            return new CollectedFile(
                AbsolutePath: f.FilePath,
                ProjectName: f.Project.Name,
                ProjectFilePath: f.Project.FilePath ?? "",
                TargetFramework: meta?.TargetFramework ?? "net10.0",
                ContainingSymbols: f.ContainingSymbols.Select(s => s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Distinct(StringComparer.Ordinal).ToList(),
                IsGenerated: f.IsGenerated,
                GeneratedText: f.GeneratedText);
        }).ToList();

        // Authored `global using` directives declare no types, so the type-graph walk never reaches
        // a file just for carrying one — yet a collected file may depend on it. Harvest them from
        // every authored file (dedicated OR mixed in with a type), excluding any already present in
        // a verbatim-copied file, and re-emit the rest as ONE synthesized, deduped file.
        var collectedPaths = new HashSet<string>(files.Select(f => f.AbsolutePath), PathComparison.Comparer);
        var globalUsings = await GlobalUsingsCollector.CollectDirectivesAsync(contributingProjects, collectedPaths, diagnostics, cancellationToken).ConfigureAwait(false);
        if (globalUsings.Count > 0)
        {
            var text = "// Global usings harvested from the source projects by chisel." + Environment.NewLine
                + string.Join(Environment.NewLine, globalUsings) + Environment.NewLine;
            files.Add(new CollectedFile(
                AbsolutePath: SynthesizedGlobalUsingsMarker,
                ProjectName: "",
                ProjectFilePath: "",
                TargetFramework: projects.FirstOrDefault()?.TargetFramework ?? "net10.0",
                ContainingSymbols: [],
                IsGenerated: true,
                GeneratedText: text));
        }

        var external = await ExternalReferenceCollector.CollectAsync(contributingProjects, walk.ExternalTypes, diagnostics, cancellationToken).ConfigureAwait(false);

        Phase($"Writing slice: {files.Count} file(s) from {contributingProjects.Count} project(s), {external.Count(r => r.PackageId is not null)} package(s)…");

        // Emit outputs. Each artifact is written independently so a failure to write one does not
        // prevent the others or the (in-memory) result from being returned.
        var fileListPath = Path.Combine(options.OutputDirectory, "files.json");
        var refPath = Path.Combine(options.OutputDirectory, "references.json");
        await diagnostics.GuardAsync("Emit", "files.json", () => FileListEmitter.WriteAsync(fileListPath, files, cancellationToken)).ConfigureAwait(false);
        await diagnostics.GuardAsync("Emit", "references.json", () => ReferenceManifestEmitter.WriteAsync(refPath, external, cancellationToken)).ConfigureAwait(false);

        IReadOnlyDictionary<string, string> mapping = new Dictionary<string, string>();
        diagnostics.Guard("Emit", "src", () => mapping = FileCopyEmitter.Copy(options.OutputDirectory, files, diagnostics, cancellationToken));

        // If we collected files but none made it into the slice, the csproj will have no <Compile>
        // items and won't build — call that out explicitly (per-file copy failures are already
        // reported, but a wholesale failure otherwise looks like a "successful" empty slice).
        if (files.Count > 0 && mapping.Count == 0)
        {
            diagnostics.Warn("Emit", $"None of the {files.Count} collected file(s) were copied into the slice; Slice.csproj will have no <Compile> items.");
        }

        diagnostics.Guard("Emit", "Slice.csproj", () =>
        {
            var csprojWarnings = CsprojGenerator.Write(options.OutputDirectory, mapping, projects, external);
            foreach (var w in csprojWarnings)
            {
                diagnostics.Warn("Csproj", w);
            }
        });

        // Give the slice a .gitignore so it behaves like a normal repo (propagates the source
        // solution's, or a minimal default).
        var gitignorePath = GitignoreEmitter.Emit(options.OutputDirectory, options.SolutionPath, diagnostics);

        var allDiagnostics = diagnostics.Items;
        var warnings = allDiagnostics
            .Where(d => d.Severity == Diagnostics.DiagnosticSeverity.Warning)
            .Select(d => d.Message)
            .ToList();

        return new SliceResult(
            SeedTypeDisplay: seed.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            SeedFilePath: seed.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath,
            InSourceTypeCount: walk.InSourceTypes.Count,
            Files: files,
            ExternalReferences: external,
            Projects: projects,
            Warnings: warnings,
            Diagnostics: allDiagnostics,
            FileListPath: fileListPath,
            ReferenceManifestPath: refPath,
            CsprojPath: Path.Combine(options.OutputDirectory, "Slice.csproj"),
            CopiedSourceRoot: Path.Combine(options.OutputDirectory, "src"),
            GitignorePath: gitignorePath);
    }

    /// <summary>
    /// Drops collected files that fall under an excluded directory subtree, reporting each as a
    /// non-fatal "Exclude" warning. Filtering here (after collection, before projects/global-usings
    /// are derived) means an excluded file's project stops contributing unless other files keep it,
    /// matching the "leave this region out" intent. The resulting slice may be incomplete — that is
    /// the caller's explicit choice, surfaced via the warnings.
    /// </summary>
    private static IReadOnlyList<SourceFileCollector.CollectedSourceFile> ApplyExclusions(
        IReadOnlyList<SourceFileCollector.CollectedSourceFile> files,
        PathExclusions exclusions,
        DiagnosticSink diagnostics)
    {
        var kept = new List<SourceFileCollector.CollectedSourceFile>(files.Count);
        foreach (var file in files)
        {
            if (exclusions.IsExcluded(file.FilePath))
            {
                diagnostics.Warn(
                    "Exclude",
                    "Excluded from collection by an --exclude path; the file is not in the slice " +
                    "(the slice may not compile without it).",
                    file.FilePath);
            }
            else
            {
                kept.Add(file);
            }
        }

        return kept;
    }

    /// <summary>
    /// The set of types whose implementations may be expanded under
    /// <see cref="ImplementationExpansion.SeedOnly"/>: the seed itself, plus — when the seed is an
    /// interface — the base interfaces it derives from. Keyed by <see cref="SymbolEqualityComparer"/>
    /// on the original definitions, matching what the worklist enqueues.
    /// </summary>
    private static IReadOnlySet<INamedTypeSymbol> BuildExpansionRoots(INamedTypeSymbol seed)
    {
        var roots = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)
        {
            (INamedTypeSymbol)seed.OriginalDefinition,
        };

        if (seed.TypeKind == TypeKind.Interface)
        {
            foreach (var baseInterface in seed.AllInterfaces)
            {
                roots.Add((INamedTypeSymbol)baseInterface.OriginalDefinition);
            }
        }

        return roots;
    }

    [GeneratedRegex(@"\(([^)]+)\)\s*$")]
    private static partial Regex TfmSuffixRegex();

    /// <summary>
    /// Collapses multi-targeted projects (multiple <see cref="Project"/> instances sharing one
    /// .csproj) down to a single target framework. Without this, the same logical type appears
    /// once per TFM and the resolver reports a spurious ambiguity. When a preferred TFM is given,
    /// the matching variant is kept; otherwise the first variant is kept and a warning is emitted.
    /// </summary>
    private static Solution ApplyTargetFrameworkFilter(Solution solution, string? preferredTfm, DiagnosticSink diagnostics)
    {
        var groups = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null)
            .GroupBy(p => p.FilePath!, PathComparison.Comparer)
            .Where(g => g.Count() > 1);

        var toRemove = new List<ProjectId>();
        foreach (var group in groups)
        {
            var variants = group.ToList();
            Project keep;

            if (preferredTfm is not null)
            {
                keep = variants.FirstOrDefault(p => string.Equals(ProjectTfm(p), preferredTfm, StringComparison.OrdinalIgnoreCase))
                    ?? variants[0];
                if (!string.Equals(ProjectTfm(keep), preferredTfm, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Warn("TargetFramework",
                        $"Project '{Path.GetFileName(group.Key)}' does not target '{preferredTfm}'; using '{ProjectTfm(keep) ?? keep.Name}' instead.",
                        group.Key);
                }
            }
            else
            {
                keep = variants[0];
                var tfms = string.Join(", ", variants.Select(p => ProjectTfm(p) ?? p.Name));
                diagnostics.Warn("TargetFramework",
                    $"Project '{Path.GetFileName(group.Key)}' multi-targets ({tfms}); slicing against '{ProjectTfm(keep) ?? keep.Name}'. Pass --tfm to choose.",
                    group.Key);
            }

            toRemove.AddRange(variants.Where(p => p.Id != keep.Id).Select(p => p.Id));
        }

        foreach (var id in toRemove)
        {
            solution = solution.RemoveProject(id);
        }

        return solution;
    }

    private static string? ProjectTfm(Project project)
    {
        // MSBuildWorkspace names multi-targeted project instances like "MyProj (net8.0)".
        var match = TfmSuffixRegex().Match(project.Name);
        return match.Success ? match.Groups[1].Value : null;
    }
}
