using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Walking;

/// <summary>
/// Walks every method body / member initializer in each declaring syntax tree of a type,
/// extracting referenced types via GetTypeInfo / GetSymbolInfo on every descendant node and handing
/// each discovered type to <c>report</c>. The caller decides what to do with them — enqueue all of
/// them (full body walk) or keep only the external ones (reference discovery for a signatures-only
/// slice). Every per-file and per-node step is fault-isolated: a node that fails to bind is
/// reported and skipped rather than aborting the file or the whole walk.
/// </summary>
public static class SyntaxTreeWalker
{
    public static async Task WalkBodiesAsync(
        INamedTypeSymbol type,
        Solution solution,
        Action<ITypeSymbol> report,
        DiagnosticSink diagnostics,
        CancellationToken cancellationToken = default)
    {
        foreach (var declRef in type.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SyntaxNode? syntax = null;
            SemanticModel? model = null;
            try
            {
                syntax = await declRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                var doc = solution.GetDocument(syntax.SyntaxTree);
                if (doc is not null)
                {
                    model = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var path = TryGetPath(declRef);
                diagnostics.Error("Body", $"Could not load semantic model for {type.Name}: {ex.GetType().Name}: {ex.Message}", path);
                continue;
            }

            if (syntax is null || model is null)
            {
                continue;
            }

            // Only walk descendants of this type's declaration (so we don't re-walk sibling
            // types in the same file that we may visit separately).
            WalkNode(syntax, model, report, diagnostics);
        }
    }

    /// <summary>
    /// Walks ONLY the attribute lists in a type's declarations (the type, its members, parameters,
    /// type parameters, return values) and reports every type referenced there — including
    /// <c>typeof(...)</c> values. This resolves attribute-argument types from SYNTAX via the
    /// semantic model, which is immune to the Roslyn <c>AttributeData.ConstructorArguments</c> NRE
    /// on <c>params</c> + null-literal argument shapes (e.g. <c>[DataRow(null)]</c>). Used in
    /// signatures mode, where method bodies aren't walked but attributes are part of the declared
    /// surface. In bodies mode the full declaration walk already covers attributes.
    /// </summary>
    public static async Task WalkAttributeTypesAsync(
        INamedTypeSymbol type,
        Solution solution,
        Action<ITypeSymbol> report,
        DiagnosticSink diagnostics,
        CancellationToken cancellationToken = default)
    {
        foreach (var declRef in type.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SyntaxNode? syntax = null;
            SemanticModel? model = null;
            try
            {
                syntax = await declRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                var doc = solution.GetDocument(syntax.SyntaxTree);
                if (doc is not null)
                {
                    model = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics.Error("Attributes", $"Could not load semantic model for {type.Name}: {ex.GetType().Name}: {ex.Message}", TryGetPath(declRef));
                continue;
            }

            if (syntax is null || model is null)
            {
                continue;
            }

            foreach (var attribute in syntax.DescendantNodes().OfType<AttributeSyntax>())
            {
                WalkNode(attribute, model, report, diagnostics);
            }
        }
    }

    internal static void WalkNode(
        SyntaxNode root,
        SemanticModel model,
        Action<ITypeSymbol> report,
        DiagnosticSink diagnostics)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            // Binding a single node must never kill the file's walk — bad/incomplete code can
            // make individual GetTypeInfo/GetSymbolInfo calls throw.
            try
            {
                var typeInfo = model.GetTypeInfo(node);
                ReportIfType(typeInfo.Type, report, diagnostics, node);
                ReportIfType(typeInfo.ConvertedType, report, diagnostics, node);

                var symbolInfo = model.GetSymbolInfo(node);
                HandleSymbol(symbolInfo.Symbol, report);
                foreach (var cand in symbolInfo.CandidateSymbols)
                {
                    HandleSymbol(cand, report);
                }

                if (node is AttributeSyntax attrSyntax &&
                    model.GetSymbolInfo(attrSyntax).Symbol is IMethodSymbol attrSymbol)
                {
                    report(attrSymbol.ContainingType);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Warn("Body", $"Skipped a syntax node that failed to bind: {ex.GetType().Name}", root.SyntaxTree.FilePath);
            }
        }
    }

    private static string? TryGetPath(SyntaxReference declRef)
    {
        try
        {
            return declRef.SyntaxTree.FilePath;
        }
        catch
        {
            return null;
        }
    }

    private static void ReportIfType(
        ITypeSymbol? t,
        Action<ITypeSymbol> report,
        DiagnosticSink diagnostics,
        SyntaxNode origin)
    {
        if (t is null)
        {
            return;
        }
        if (t.TypeKind == TypeKind.Dynamic)
        {
            // Key the diagnostic on the FILE (not file:line) so a file with many `dynamic` sites
            // collapses to a single warning via the sink's dedup, instead of flooding the summary.
            diagnostics.Warn(
                "Body",
                "uses `dynamic` — reflective dependencies are not traced.",
                origin.SyntaxTree.FilePath);
            return;
        }
        report(t);
    }

    private static void HandleSymbol(ISymbol? symbol, Action<ITypeSymbol> report)
    {
        switch (symbol)
        {
            case null:
                return;
            case INamedTypeSymbol named:
                report(named);
                break;
            case IMethodSymbol m:
                report(m.ContainingType);
                report(m.ReturnType);
                foreach (var p in m.Parameters)
                {
                    report(p.Type);
                }
                foreach (var ta in m.TypeArguments)
                {
                    report(ta);
                }
                break;
            case IPropertySymbol p:
                report(p.ContainingType);
                report(p.Type);
                break;
            case IFieldSymbol f:
                report(f.ContainingType);
                report(f.Type);
                break;
            case IEventSymbol e:
                report(e.ContainingType);
                report(e.Type);
                break;
        }
    }
}
