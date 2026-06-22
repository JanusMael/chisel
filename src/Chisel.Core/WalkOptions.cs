namespace Bennewitz.Ninja.Chisel;

/// <summary>How deep the dependency walk follows each collected type.</summary>
public enum WalkDepth
{
    /// <summary>
    /// Collect only declaration/signature types: base types &amp; interfaces, generic constraints
    /// and type arguments, member types (field/property/event types, method parameter &amp; return
    /// types, indexer parameters), and attribute types. Does NOT follow what method bodies
    /// reference. Produces a focused "contract + shape" slice; it may not compile standalone
    /// because types used only inside bodies are treated as external (left out).
    /// </summary>
    Signatures,

    /// <summary>
    /// Everything in <see cref="Signatures"/> plus every type referenced inside method bodies,
    /// transitively (the full reachable usage/call graph). Produces a self-compilable slice but
    /// collects far more.
    /// </summary>
    Bodies,
}

/// <summary>Which interfaces / base classes get expanded to their concrete implementations.</summary>
public enum ImplementationExpansion
{
    /// <summary>
    /// Expand only the seed type (and, when the seed is an interface, the base interfaces it
    /// derives from) to their implementations. Interfaces/classes encountered deeper in the graph
    /// — e.g. a property's type — are collected as declarations only, not expanded.
    /// </summary>
    SeedOnly,

    /// <summary>Expand every interface / abstract / non-sealed class reached anywhere in the graph.</summary>
    All,

    /// <summary>Never pull implementations; collect only the literal type closure.</summary>
    None,
}
