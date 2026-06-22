using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Walking;

/// <summary>
/// Walks the declared surface of a type: base types, interfaces, generic constraints,
/// type arguments, every member's referenced types, nested types, and all attributes
/// (including typeof() values inside attribute arguments).
///
/// Every step is individually fault-isolated. A single throwing member or symbol property (Roslyn
/// can throw on malformed / partially-loaded symbols) must NOT abort the whole type — otherwise a
/// type's attributes and later members would be silently dropped along with the failure.
/// </summary>
public static class TypeMemberWalker
{
    public static void WalkMembers(INamedTypeSymbol type, SymbolWorklist worklist, DiagnosticSink diagnostics)
    {
        var path = TryGetPath(type);

        void Guarded(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                diagnostics.Warn("Walk", $"Skipped {what} of {type.Name}: {ex.GetType().Name}: {ex.Message}", path);
            }
        }

        // A nested type cannot compile without its enclosing type's declaration, so the
        // enclosing type (and its file) must be in the slice. Walk the whole containing chain.
        Guarded("containing type", () => worklist.Enqueue(type.ContainingType));

        Guarded("base type", () => worklist.Enqueue(type.BaseType));

        // AllInterfaces (not just directly-declared Interfaces): an interface inherited through an
        // EXTERNAL base class is never reached by walking base types (external types aren't walked),
        // so a constructed interface like ExternalBase : ITagged<InSourceType> would otherwise drop
        // InSourceType from the slice. Enqueue is deduplicated, so this is idempotent.
        Guarded("interfaces", () =>
        {
            foreach (var iface in type.AllInterfaces)
            {
                worklist.Enqueue(iface);
            }
        });

        Guarded("type parameters", () =>
        {
            foreach (var tp in type.TypeParameters)
            {
                EnqueueTypeParameter(tp, worklist, diagnostics, type.Name, path);
            }
        });

        Guarded("type arguments", () =>
        {
            foreach (var ta in type.TypeArguments)
            {
                worklist.Enqueue(ta);
            }
        });

        // The type's own attributes — walked independently so an earlier failure can't drop them.
        Guarded("attributes", () => WalkAttributes(type.GetAttributes(), worklist, diagnostics, type.Name, path));

        ImmutableArray<ISymbol> members;
        try
        {
            members = type.GetMembers();
        }
        catch (Exception ex)
        {
            diagnostics.Warn("Walk", $"Could not enumerate members of {type.Name}: {ex.GetType().Name}", path);
            return;
        }

        foreach (var member in members)
        {
            Guarded($"member '{member.Name}'", () => WalkMember(member, worklist, diagnostics, type.Name, path));
        }
    }

    private static void WalkMember(ISymbol member, SymbolWorklist worklist, DiagnosticSink diagnostics, string typeName, string? path)
    {
        var owner = $"{typeName}.{member.Name}";
        switch (member)
        {
            case IFieldSymbol f:
                worklist.Enqueue(f.Type);
                WalkAttributes(f.GetAttributes(), worklist, diagnostics, owner, path);
                break;
            case IPropertySymbol p:
                worklist.Enqueue(p.Type);
                WalkAttributes(p.GetAttributes(), worklist, diagnostics, owner, path);
                foreach (var param in p.Parameters)
                {
                    worklist.Enqueue(param.Type);
                    WalkAttributes(param.GetAttributes(), worklist, diagnostics, owner, path);
                }
                break;
            case IEventSymbol e:
                worklist.Enqueue(e.Type);
                WalkAttributes(e.GetAttributes(), worklist, diagnostics, owner, path);
                break;
            case IMethodSymbol m:
                worklist.Enqueue(m.ReturnType);
                WalkAttributes(m.GetAttributes(), worklist, diagnostics, owner, path);
                WalkAttributes(m.GetReturnTypeAttributes(), worklist, diagnostics, owner, path);
                foreach (var param in m.Parameters)
                {
                    worklist.Enqueue(param.Type);
                    WalkAttributes(param.GetAttributes(), worklist, diagnostics, owner, path);
                }
                foreach (var tp in m.TypeParameters)
                {
                    EnqueueTypeParameter(tp, worklist, diagnostics, owner, path);
                }
                foreach (var ta in m.TypeArguments)
                {
                    worklist.Enqueue(ta);
                }
                break;
            case INamedTypeSymbol nested:
                worklist.Enqueue(nested);
                break;
        }
    }

    private static void EnqueueTypeParameter(ITypeParameterSymbol tp, SymbolWorklist worklist, DiagnosticSink diagnostics, string owner, string? path)
    {
        foreach (var c in tp.ConstraintTypes)
        {
            worklist.Enqueue(c);
        }
        WalkAttributes(tp.GetAttributes(), worklist, diagnostics, owner, path);
    }

    public static void WalkAttributes(
        IEnumerable<AttributeData> attributes,
        SymbolWorklist worklist,
        DiagnosticSink diagnostics,
        string owner,
        string? path)
    {
        foreach (var attr in attributes)
        {
            // The attribute TYPE — recorded first and in its own guard, so a failure reading the
            // arguments below can't also drop the attribute-class dependency.
            INamedTypeSymbol? attrClass = null;
            try
            {
                attrClass = attr.AttributeClass;
                if (attrClass is not null)
                {
                    worklist.Enqueue(attrClass);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Warn("Walk", $"Skipped an attribute class on {owner}: {ex.GetType().Name}", path);
            }

            // The arguments. Roslyn can throw (e.g. NullReferenceException) here when an attribute
            // application is malformed or references a type it can't bind. Isolate it so only this
            // attribute's argument-type dependencies are lost, and name the attribute + owner + file
            // so the offending usage can be found.
            try
            {
                foreach (var ca in attr.ConstructorArguments)
                {
                    EnqueueTypedConstant(ca, worklist);
                }
                foreach (var na in attr.NamedArguments)
                {
                    EnqueueTypedConstant(na.Value, worklist);
                }
            }
            catch (Exception ex)
            {
                var name = attrClass?.Name ?? "an";
                diagnostics.Warn("Walk", $"Skipped {name} attribute's arguments on {owner}: {ex.GetType().Name}", path);
            }
        }
    }

    private static void EnqueueTypedConstant(TypedConstant constant, SymbolWorklist worklist)
    {
        // The declared type of the argument itself — e.g. an in-source enum passed as an attribute
        // value (`[Attr(MyEnum.X)]`) whose parameter is object/Enum-typed, so the enum type is not
        // otherwise reachable from the attribute class's signature. Primitives resolve to BCL types
        // and are harmlessly dropped as external leaves.
        worklist.Enqueue(constant.Type);

        switch (constant.Kind)
        {
            case TypedConstantKind.Type:
                if (constant.Value is ITypeSymbol t)
                {
                    worklist.Enqueue(t);
                }
                break;
            case TypedConstantKind.Array:
                foreach (var v in constant.Values)
                {
                    EnqueueTypedConstant(v, worklist);
                }
                break;
        }
    }

    private static string? TryGetPath(INamedTypeSymbol type)
    {
        try
        {
            return type.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
        }
        catch
        {
            return null;
        }
    }
}
