using Microsoft.CodeAnalysis;
using Bennewitz.Ninja.Chisel.Diagnostics;
using Bennewitz.Ninja.Chisel.Resolution;

namespace Bennewitz.Ninja.Chisel.Walking;

public sealed record WalkResult(
    IReadOnlyList<INamedTypeSymbol> InSourceTypes,
    IReadOnlyList<INamedTypeSymbol> ExternalTypes);

public sealed class DependencyWalker
{
    private readonly Solution _solution;
    private readonly AssemblyIndex _assemblyIndex;
    private readonly WalkDepth _walkDepth;
    private readonly ImplementationExpansion _expansion;
    private readonly IReadOnlySet<INamedTypeSymbol> _expansionRoots;
    private readonly DiagnosticSink _diagnostics;

    public DependencyWalker(
        Solution solution,
        AssemblyIndex assemblyIndex,
        WalkDepth walkDepth,
        ImplementationExpansion expansion,
        IReadOnlySet<INamedTypeSymbol> expansionRoots,
        DiagnosticSink diagnostics)
    {
        _solution = solution;
        _assemblyIndex = assemblyIndex;
        _walkDepth = walkDepth;
        _expansion = expansion;
        _expansionRoots = expansionRoots;
        _diagnostics = diagnostics;
    }

    public async Task<WalkResult> WalkAsync(
        INamedTypeSymbol seed,
        Action<string>? onActivity = null,
        CancellationToken cancellationToken = default)
    {
        var worklist = new SymbolWorklist();
        worklist.Enqueue(seed);

        var inSource = new List<INamedTypeSymbol>();
        var external = new List<INamedTypeSymbol>();
        var externalSeen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        void RecordExternal(INamedTypeSymbol type)
        {
            if (externalSeen.Add(type))
            {
                external.Add(type);
            }
        }

        // Signatures-only reference discovery: scan a collected file's bodies but keep ONLY the
        // external types they reference (so the emitted csproj has the packages the copied file
        // actually needs). In-source body types are intentionally NOT collected in this mode.
        void ReportExternalOnly(ITypeSymbol referenced)
        {
            foreach (var named in SymbolWorklist.Flatten(referenced))
            {
                if (!_assemblyIndex.IsInSource(named.ContainingAssembly))
                {
                    RecordExternal(named);
                }
            }
        }

        while (worklist.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_assemblyIndex.IsInSource(current.ContainingAssembly))
            {
                RecordExternal(current);
                continue;
            }

            // The type itself is recorded BEFORE we walk its edges, so even if the walk of this
            // one type fails, its source file is still collected — we only lose further edges.
            inSource.Add(current);

            // Cheap status update so a heartbeat can show what's in flight.
            onActivity?.Invoke($"walking {ActivityName(current)} — {inSource.Count} collected, {worklist.Count} queued");

            var label = SafeLabel(current);
            // WalkMembers is internally fault-isolated per member/step; the outer guard is a final
            // backstop so an unexpected throw still can't abort the whole walk.
            _diagnostics.Guard("Walk", label, () => TypeMemberWalker.WalkMembers(current, worklist, _diagnostics));

            // Body walk. In Bodies mode every referenced type is enqueued (transitive, self-
            // compilable slice). In Signatures mode we still scan bodies but keep only EXTERNAL
            // references — in-source "usages" are not collected, but their packages are recorded.
            var report = _walkDepth == WalkDepth.Bodies
                ? (Action<ITypeSymbol>)(t => worklist.Enqueue(t))
                : ReportExternalOnly;
            await _diagnostics.GuardAsync("Walk", label, () =>
                SyntaxTreeWalker.WalkBodiesAsync(current, _solution, report, _diagnostics, cancellationToken)).ConfigureAwait(false);

            // In Signatures mode the body walk above doesn't enqueue in-source types, so attribute
            // arguments that reference in-source types via typeof are recovered here from SYNTAX —
            // immune to the Roslyn ConstructorArguments NRE that the symbol-based WalkMembers can hit
            // on params/null attribute shapes. (Bodies mode already covers attributes in its full
            // declaration walk.)
            if (_walkDepth != WalkDepth.Bodies)
            {
                await _diagnostics.GuardAsync("Attributes", label, () =>
                    SyntaxTreeWalker.WalkAttributeTypesAsync(current, _solution, t => worklist.Enqueue(t), _diagnostics, cancellationToken)).ConfigureAwait(false);
            }

            if (ShouldExpand(current))
            {
                // The expensive, opaque step — name it explicitly so the heartbeat shows it during
                // the (potentially multi-second) solution-wide implementation scan.
                onActivity?.Invoke($"finding implementations of {ActivityName(current)} — {inSource.Count} collected");
                await _diagnostics.GuardAsync("Derived", label, () =>
                    DerivedTypeFinder.EnqueueDerivedAsync(current, _solution, worklist, cancellationToken)).ConfigureAwait(false);
            }
        }

        return new WalkResult(inSource, external);
    }

    private bool ShouldExpand(INamedTypeSymbol current)
    {
        // Only interfaces and inheritable classes can have implementations/derivations. A sealed
        // class never does, so skip the (solution-wide, expensive) SymbolFinder scan for it.
        var expandable = current.TypeKind == TypeKind.Interface
            || (current.TypeKind == TypeKind.Class && !current.IsSealed);
        if (!expandable)
        {
            return false;
        }

        return _expansion switch
        {
            ImplementationExpansion.None => false,
            ImplementationExpansion.All => true,
            // current is already an OriginalDefinition (the worklist normalizes), matching the roots.
            ImplementationExpansion.SeedOnly => _expansionRoots.Contains(current),
            _ => false,
        };
    }

    /// <summary>
    /// A short, human-friendly name for progress display. Compiler-synthesized types (notably
    /// anonymous types) have an empty <see cref="ISymbol.Name"/>, which would render as a blank in
    /// the heartbeat — fall back to a readable placeholder.
    /// </summary>
    private static string ActivityName(INamedTypeSymbol symbol)
        => !string.IsNullOrEmpty(symbol.Name) ? symbol.Name
            : symbol.IsAnonymousType ? "<anonymous type>"
            : "<unnamed type>";

    /// <summary>
    /// A diagnostic label for a type: its fully-qualified name plus its first declaring file path,
    /// so an error like "NullReferenceException — Foo (C:\src\Foo.cs)" points at the actual file.
    /// </summary>
    private static string SafeLabel(INamedTypeSymbol symbol)
    {
        string name;
        try
        {
            name = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        catch
        {
            name = symbol.Name;
        }

        var path = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
        return string.IsNullOrEmpty(path) ? name : $"{name} ({path})";
    }
}
