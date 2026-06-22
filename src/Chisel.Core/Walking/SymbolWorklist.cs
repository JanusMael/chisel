using Microsoft.CodeAnalysis;

namespace Bennewitz.Ninja.Chisel.Walking;

/// <summary>
/// Deduplicating FIFO worklist of named type symbols. Normalizes to OriginalDefinition
/// so List&lt;int&gt; and List&lt;string&gt; collapse to one queue entry.
/// </summary>
public sealed class SymbolWorklist
{
    private readonly Queue<INamedTypeSymbol> _queue = new();
    private readonly HashSet<INamedTypeSymbol> _enqueued = new(SymbolEqualityComparer.Default);

    /// <summary>
    /// Enqueues every named type reachable from <paramref name="symbol"/>'s structure: the type
    /// itself (as its OriginalDefinition), the type arguments of a constructed generic, array
    /// element types, pointer/by-ref targets, and function-pointer signature types. Returns true
    /// if at least one new named type was added.
    /// </summary>
    public bool Enqueue(ITypeSymbol? symbol)
    {
        var added = false;
        foreach (var named in Flatten(symbol))
        {
            if (_enqueued.Add(named))
            {
                _queue.Enqueue(named);
                added = true;
            }
        }
        return added;
    }

    /// <summary>
    /// Decomposes a type reference into the distinct named types it implies — the type itself (as
    /// its OriginalDefinition), the type arguments of a constructed generic, array element types,
    /// pointer/by-ref targets, and function-pointer signature types. Type parameters and dynamic
    /// are dropped. Used both to enqueue and (by the walker) to classify referenced types.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> Flatten(ITypeSymbol? symbol)
    {
        switch (symbol)
        {
            case null:
            case ITypeParameterSymbol:
            case IDynamicTypeSymbol:
                yield break;
            case IArrayTypeSymbol arr:
                foreach (var x in Flatten(arr.ElementType))
                {
                    yield return x;
                }
                yield break;
            case IPointerTypeSymbol ptr:
                foreach (var x in Flatten(ptr.PointedAtType))
                {
                    yield return x;
                }
                yield break;
            case IFunctionPointerTypeSymbol fnPtr:
                foreach (var x in Flatten(fnPtr.Signature.ReturnType))
                {
                    yield return x;
                }
                foreach (var p in fnPtr.Signature.Parameters)
                {
                    foreach (var x in Flatten(p.Type))
                    {
                        yield return x;
                    }
                }
                yield break;
            case INamedTypeSymbol named:
                yield return (INamedTypeSymbol)named.OriginalDefinition;
                // Type arguments of a constructed generic (e.g. the Customer in
                // Dictionary<string, Customer>) are referenced types in their own right and are
                // NOT reachable from the OriginalDefinition.
                if (!named.IsDefinition)
                {
                    foreach (var ta in named.TypeArguments)
                    {
                        foreach (var x in Flatten(ta))
                        {
                            yield return x;
                        }
                    }
                }
                yield break;
        }
    }

    public bool TryDequeue(out INamedTypeSymbol symbol)
    {
        if (_queue.Count == 0)
        {
            symbol = null!;
            return false;
        }
        symbol = _queue.Dequeue();
        return true;
    }

    public int Count => _queue.Count;
}
