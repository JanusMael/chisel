using Microsoft.CodeAnalysis;
using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Collection;

public enum SourceGeneratorPolicy
{
    /// <summary>Skip generator output files entirely.</summary>
    Skip,
    /// <summary>Materialize generator output into the slice directory as plain .cs.</summary>
    Materialize,
    /// <summary>Skip files; the generator should be re-run by the downstream build.</summary>
    Reference,
}

public static class SourceFileCollector
{
    public sealed record CollectedSourceFile(
        string FilePath,
        Project Project,
        bool IsGenerated,
        IReadOnlyList<INamedTypeSymbol> ContainingSymbols,
        // For source-generated trees under the Materialize policy, the generator output text to
        // write to disk (the FilePath is a synthetic obj path that is not present on disk).
        string? GeneratedText = null);

    public static async Task<IReadOnlyList<CollectedSourceFile>> CollectAsync(
        Solution solution,
        IEnumerable<INamedTypeSymbol> inSourceTypes,
        SourceGeneratorPolicy generatorPolicy,
        DiagnosticSink diagnostics,
        CancellationToken cancellationToken = default)
    {
        // Authoritative index of source-generator output, keyed by the generated document's
        // FilePath. This is more reliable than guessing from `GetDocument(tree) == null` plus a
        // File.Exists check: it correctly identifies generated trees even when the project sets
        // <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles> (which writes them to obj).
        // Generators were already run while building compilations during the walk, so these calls
        // hit the workspace's cache.
        var generatedByPath = new Dictionary<string, SourceGeneratedDocument>(PathComparison.Comparer);
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }
            // Enumerating generator output can fail for a broken project; degrade to "no generated
            // docs for this project" rather than aborting the whole collection.
            await diagnostics.GuardAsync("Collect", project.Name, async () =>
            {
                var genDocs = await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var gd in genDocs)
                {
                    if (gd.FilePath is { } fp)
                    {
                        generatedByPath[fp] = gd;
                    }
                }
            }).ConfigureAwait(false);
        }

        // Group by file path so partial types collapse and we accumulate the symbols per file.
        var byPath = new Dictionary<string, (Project Project, bool IsGenerated, string? GeneratedText, List<INamedTypeSymbol> Symbols)>(PathComparison.Comparer);
        var skippedGeneratedByProject = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var type in inSourceTypes)
        {
            foreach (var declRef in type.DeclaringSyntaxReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Resolving / materializing one declaration must not abort the whole collection.
                await diagnostics.GuardAsync("Collect", type.Name, () => ProcessDeclRefAsync(type, declRef)).ConfigureAwait(false);
            }
        }

        var files = byPath
            .Select(kv => new CollectedSourceFile(kv.Key, kv.Value.Project, kv.Value.IsGenerated, kv.Value.Symbols, kv.Value.GeneratedText))
            .ToList();

        ReportSkippedGenerated(skippedGeneratedByProject, generatorPolicy, diagnostics);
        return files;

        async Task ProcessDeclRefAsync(INamedTypeSymbol type, SyntaxReference declRef)
        {
            var tree = declRef.SyntaxTree;
            var path = tree.FilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var isGenerated = generatedByPath.TryGetValue(path, out var genDoc);

            if (isGenerated && generatorPolicy != SourceGeneratorPolicy.Materialize)
            {
                // Record the skip so we can warn (once per project) that the slice depends on
                // generator output that won't be present unless the generator runs.
                var projName = genDoc!.Project.Name;
                if (!skippedGeneratedByProject.TryGetValue(projName, out var set))
                {
                    set = new HashSet<string>(PathComparison.Comparer);
                    skippedGeneratedByProject[projName] = set;
                }
                set.Add(path);
                return;
            }

            Project? project;
            string? generatedText = null;
            if (isGenerated)
            {
                project = genDoc!.Project;
                var text = await genDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                generatedText = text.ToString();
            }
            else
            {
                var doc = solution.GetDocument(tree);
                // A null document whose file is absent is a synthetic tree we can't materialize
                // (and isn't a known generated doc) — skip it.
                if (doc is null && !File.Exists(path))
                {
                    return;
                }
                project = doc?.Project ?? FindProjectByTree(solution, tree);
            }

            if (project is null)
            {
                return;
            }

            if (!byPath.TryGetValue(path, out var entry))
            {
                entry = (project, isGenerated, generatedText, new List<INamedTypeSymbol>());
                byPath[path] = entry;
            }
            entry.Symbols.Add(type);
        }
    }

    private static void ReportSkippedGenerated(
        Dictionary<string, HashSet<string>> skippedGeneratedByProject,
        SourceGeneratorPolicy policy,
        DiagnosticSink diagnostics)
    {
        foreach (var (projectName, paths) in skippedGeneratedByProject)
        {
            diagnostics.Warn(
                "SourceGenerators",
                $"Skipped {paths.Count} source-generated file(s) from project '{projectName}' " +
                $"({policy} policy). The slice will not compile unless the generator runs against it; " +
                $"use --source-generators materialize to write the generated code into the slice.",
                projectName);
        }
    }

    private static Project? FindProjectByTree(Solution solution, SyntaxTree tree)
    {
        foreach (var project in solution.Projects)
        {
            // Best-effort: ProjectFilePath comparison.
            var dir = project.FilePath is { } pfp ? Path.GetDirectoryName(pfp) : null;
            if (dir is not null && tree.FilePath.StartsWith(dir, PathComparison.Comparison))
            {
                return project;
            }
        }
        return null;
    }
}
