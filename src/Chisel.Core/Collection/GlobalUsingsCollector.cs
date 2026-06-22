using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Collection;

/// <summary>
/// Extracts the <c>global using</c> directives authored across the contributing projects so they
/// can be re-emitted into the slice as a single synthesized file.
///
/// Global usings are compilation-wide and declare no types, so the type-graph walk never reaches a
/// file just because it carries one — yet a collected file may rely on it (e.g. <c>StringBuilder</c>
/// bound only via <c>global using System.Text;</c>). We harvest the directives from EVERY authored
/// (non-generated) file, whether dedicated (<c>GlobalUsings.cs</c>) or mixed in with a type, so a
/// global using living next to a class is no longer lost. Directives already present in a file that
/// is copied verbatim into the slice are excluded, so nothing is duplicated (avoids CS0105).
/// </summary>
public static class GlobalUsingsCollector
{
    /// <summary>
    /// Returns the distinct <c>global using</c> directives to synthesize into the slice — the ones
    /// not already carried by a verbatim-copied file in <paramref name="collectedFilePaths"/>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CollectDirectivesAsync(
        IEnumerable<Project> contributingProjects,
        IReadOnlySet<string> collectedFilePaths,
        DiagnosticSink diagnostics,
        CancellationToken cancellationToken = default)
    {
        // Already present in a copied file (don't re-emit) vs. needing synthesis.
        var present = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(PathComparison.Comparer);

        foreach (var project in contributingProjects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = document.FilePath;
                if (string.IsNullOrWhiteSpace(path) || !seenPaths.Add(path))
                {
                    continue;
                }

                // Skip SDK-generated files (obj/.../*.GlobalUsings.g.cs); the slice regenerates
                // those itself via <ImplicitUsings> and re-emitting would duplicate (CS0105).
                if (IsGeneratedFile(path))
                {
                    continue;
                }

                var isCopiedVerbatim = collectedFilePaths.Contains(path);

                await diagnostics.GuardAsync("GlobalUsings", path, async () =>
                {
                    var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                    if (root is not CompilationUnitSyntax unit)
                    {
                        return;
                    }

                    foreach (var directive in unit.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
                    {
                        var text = directive.NormalizeWhitespace().ToFullString().Trim();
                        if (isCopiedVerbatim)
                        {
                            present.Add(text);
                        }
                        else
                        {
                            candidates.Add(text);
                        }
                    }
                }).ConfigureAwait(false);
            }
        }

        candidates.ExceptWith(present);
        return candidates.OrderBy(d => d, StringComparer.Ordinal).ToList();
    }

    private static bool IsGeneratedFile(string path)
    {
        if (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // A path segment named "obj" indicates the MSBuild intermediate output directory.
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }
}
