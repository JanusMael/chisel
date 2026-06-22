using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Bennewitz.Ninja.Chisel.Walking;

public static class DerivedTypeFinder
{
    public static async Task EnqueueDerivedAsync(
        INamedTypeSymbol type,
        Solution solution,
        SymbolWorklist worklist,
        CancellationToken cancellationToken = default)
    {
        // Scope the (solution-wide, expensive) SymbolFinder scan to only the projects that could
        // possibly contain a derived type: the project that declares `type` plus every project that
        // transitively depends on it. An implementer must reference the declaring assembly, so it
        // cannot live outside that closure. On a large solution this is a major speedup. A null set
        // means "search everywhere" (the safe fallback when we can't map the symbol to a project).
        var scope = GetCandidateProjects(type, solution);

        if (type.TypeKind == TypeKind.Interface)
        {
            // Transitive concrete + abstract implementations.
            var impls = await SymbolFinder.FindImplementationsAsync(
                type, solution, transitive: true, projects: scope, cancellationToken).ConfigureAwait(false);
            foreach (var impl in impls)
            {
                if (impl is INamedTypeSymbol named)
                {
                    worklist.Enqueue(named);
                }
            }

            // Interfaces that extend this interface.
            var derivedIfaces = await SymbolFinder.FindDerivedInterfacesAsync(
                type, solution, transitive: true, projects: scope, cancellationToken).ConfigureAwait(false);
            foreach (var di in derivedIfaces)
            {
                worklist.Enqueue(di);
            }
        }
        else if (type.TypeKind == TypeKind.Class)
        {
            // For non-sealed classes (including abstract), pull in derived classes.
            // Sealed concrete classes can't have derivations — skip the (expensive) call.
            if (!type.IsSealed)
            {
                var derived = await SymbolFinder.FindDerivedClassesAsync(
                    type, solution, transitive: true, projects: scope, cancellationToken).ConfigureAwait(false);
                foreach (var d in derived)
                {
                    worklist.Enqueue(d);
                }
            }
        }
    }

    private static IImmutableSet<Project>? GetCandidateProjects(INamedTypeSymbol type, Solution solution)
    {
        if (type.ContainingAssembly is null)
        {
            return null;
        }

        var declaringProject = solution.GetProject(type.ContainingAssembly);
        if (declaringProject is null)
        {
            return null;
        }

        var graph = solution.GetProjectDependencyGraph();
        var builder = ImmutableHashSet.CreateBuilder<Project>();
        builder.Add(declaringProject);
        foreach (var id in graph.GetProjectsThatTransitivelyDependOnThisProject(declaringProject.Id))
        {
            if (solution.GetProject(id) is { } p)
            {
                builder.Add(p);
            }
        }
        return builder.ToImmutable();
    }
}
