using Microsoft.CodeAnalysis;
using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Resolution;

public sealed class AssemblyIndex
{
    private readonly HashSet<string> _inSourceAssemblyNames;
    private readonly HashSet<AssemblyIdentity> _inSourceAssemblyIdentities;

    private AssemblyIndex(HashSet<string> names, HashSet<AssemblyIdentity> identities)
    {
        _inSourceAssemblyNames = names;
        _inSourceAssemblyIdentities = identities;
    }

    public static async Task<AssemblyIndex> BuildAsync(
        Solution solution,
        DiagnosticSink? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<AssemblyIdentity>();
        var gate = new object();

        var projects = solution.Projects.Where(p => p.Language == LanguageNames.CSharp);

        // Compiling every project is the single slowest phase (it's a full semantic compile of the
        // codebase). The projects are independent, GetCompilationAsync is thread-safe, and warming
        // the workspace's compilation cache here speeds up the resolver and the walk too — so do it
        // in parallel, bounded so we don't thrash CPU/memory on a large solution.
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        };

        await Parallel.ForEachAsync(projects, parallelOptions, async (project, ct) =>
        {
            // A project that fails to compile must not abort index construction — its types just
            // won't be recognized as in-source (they'll be treated as external leaves). Report it.
            try
            {
                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation is null)
                {
                    diagnostics?.Warn("AssemblyIndex",
                        "No compilation available; this project's types will be treated as external.", project.Name);
                    return;
                }

                var identity = compilation.Assembly.Identity;
                lock (gate)
                {
                    identities.Add(identity);
                    names.Add(identity.Name);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics?.Warn("AssemblyIndex",
                    $"Failed to load compilation ({ex.GetType().Name}); this project's types will be treated as external.",
                    project.Name);
            }
        }).ConfigureAwait(false);

        return new AssemblyIndex(names, identities);
    }

    public bool IsInSource(IAssemblySymbol? assembly)
    {
        if (assembly is null)
        {
            return false;
        }

        var identity = assembly.Identity;

        // Primary: full-identity match.
        if (_inSourceAssemblyIdentities.Contains(identity))
        {
            return true;
        }

        // Fallback: simple-name match, but ONLY for unsigned assemblies (empty public key
        // token). Source projects in a workspace are unsigned by default; a signed NuGet
        // assembly that happens to share a simple name with a source project must NOT be
        // misclassified as in-source (which would chase its whole metadata graph as if it
        // were our code). The version may differ across compilation snapshots, hence the
        // name fallback for the unsigned case.
        return identity.PublicKeyToken.IsEmpty
            && _inSourceAssemblyNames.Contains(identity.Name);
    }
}
