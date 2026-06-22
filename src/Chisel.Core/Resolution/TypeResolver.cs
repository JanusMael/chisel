using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Bennewitz.Ninja.Chisel.Resolution;

public sealed class TypeResolutionException : Exception
{
    public IReadOnlyList<INamedTypeSymbol> Candidates { get; }

    public TypeResolutionException(string message, IReadOnlyList<INamedTypeSymbol>? candidates = null)
        : base(message)
    {
        Candidates = candidates ?? Array.Empty<INamedTypeSymbol>();
    }
}

public static class TypeResolver
{
    public static async Task<INamedTypeSymbol> ResolveAsync(
        Solution solution,
        string fullyQualifiedTypeName,
        string? projectFilter = null,
        AssemblyIndex? inSourceAssemblies = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullyQualifiedTypeName))
        {
            throw new ArgumentException("Type name must be provided.", nameof(fullyQualifiedTypeName));
        }

        var metadataCandidates = BuildMetadataNameCandidates(fullyQualifiedTypeName).ToList();
        var matches = new List<INamedTypeSymbol>(4);
        // Dedup by a cross-compilation key, NOT SymbolEqualityComparer: a multi-targeted
        // project surfaces the SAME logical type as DISTINCT symbols per TFM compilation, and
        // those are never reference-equal. Collapsing on (display name + declaring file) keeps
        // genuinely-distinct types (different source files) as separate ambiguous candidates.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddIfNew(INamedTypeSymbol type)
        {
            if (inSourceAssemblies is not null && !inSourceAssemblies.IsInSource(type.ContainingAssembly))
            {
                return;
            }
            if (seen.Add(DedupKey(type)))
            {
                matches.Add(type);
            }
        }

        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }

            if (projectFilter is not null && !string.Equals(project.Name, projectFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var metaName in metadataCandidates)
            {
                foreach (var type in compilation.GetTypesByMetadataName(metaName))
                {
                    AddIfNew(type);
                }
            }
        }

        if (matches.Count == 0)
        {
            // Last-resort: search by short display name across declarations.
            var shortName = ExtractShortName(fullyQualifiedTypeName);
            var target = NormalizeForCompare(fullyQualifiedTypeName);
            await foreach (var sym in EnumerateDeclarationsAsync(solution, shortName, projectFilter, cancellationToken))
            {
                if (sym is INamedTypeSymbol named &&
                    string.Equals(BareDisplayName(named), target, StringComparison.Ordinal))
                {
                    AddIfNew(named);
                }
            }
        }

        return matches.Count switch
        {
            0 => throw new TypeResolutionException(
                $"Type '{fullyQualifiedTypeName}' was not found in any project of the loaded solution." +
                (projectFilter is null ? "" : $" (project filter: {projectFilter})")),
            1 => matches[0],
            _ => throw new TypeResolutionException(BuildAmbiguityMessage(fullyQualifiedTypeName, matches), matches),
        };
    }

    private static async IAsyncEnumerable<ISymbol> EnumerateDeclarationsAsync(
        Solution solution,
        string shortName,
        string? projectFilter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }

            if (projectFilter is not null && !string.Equals(project.Name, projectFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var declarations = await SymbolFinder.FindDeclarationsAsync(project, shortName, ignoreCase: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var d in declarations)
            {
                yield return d;
            }
        }
    }

    internal static IEnumerable<string> BuildMetadataNameCandidates(string input)
    {
        // Drop generic argument list: "Foo<T,U>" -> "Foo" with implicit arity inference.
        var (bareName, arity) = StripGenericList(input);

        // Original form (already in metadata form possibly).
        yield return bareName + (arity > 0 ? "`" + arity : "");

        // If no arity was supplied via <...>, also try the bare name (closed generic case).
        if (arity == 0)
        {
            yield return bareName;
        }

        // Try interpreting trailing "." segments as nested types.
        if (bareName.Contains('.'))
        {
            var withNested = bareName;
            while (true)
            {
                var lastDot = withNested.LastIndexOf('.');
                if (lastDot <= 0)
                {
                    break;
                }

                withNested = withNested[..lastDot] + "+" + withNested[(lastDot + 1)..];
                yield return withNested + (arity > 0 ? "`" + arity : "");
                if (arity == 0)
                {
                    yield return withNested;
                }
            }
        }
    }

    private static (string Name, int Arity) StripGenericList(string input)
    {
        var lt = input.IndexOf('<');
        if (lt < 0)
        {
            return (input, 0);
        }

        var gt = input.LastIndexOf('>');
        if (gt < lt)
        {
            return (input, 0);
        }

        var name = input[..lt];
        var inner = input.Substring(lt + 1, gt - lt - 1);
        // Arity = top-level commas + 1. This holds for bound forms (Foo<T> → 1, Foo<T,U> → 2)
        // AND for unbound forms (Foo<> → 1, Foo<,> → 2). The presence of angle brackets always
        // implies at least one type parameter.
        var arity = 1;
        var depth = 0;
        foreach (var ch in inner)
        {
            if (ch == '<') depth++;
            else if (ch == '>') depth--;
            else if (ch == ',' && depth == 0) arity++;
        }

        return (name, arity);
    }

    private static string ExtractShortName(string fullyQualifiedTypeName)
    {
        var (bare, _) = StripGenericList(fullyQualifiedTypeName);
        var lastDot = bare.LastIndexOf('.');
        var lastPlus = bare.LastIndexOf('+');
        var idx = Math.Max(lastDot, lastPlus);
        return idx < 0 ? bare : bare[(idx + 1)..];
    }

    private static string NormalizeForCompare(string fullyQualifiedTypeName)
    {
        var (bare, _) = StripGenericList(fullyQualifiedTypeName);
        return bare.Replace('+', '.');
    }

    /// <summary>
    /// The symbol's namespace-qualified name with the generic argument list stripped, so it
    /// can be compared against <see cref="NormalizeForCompare"/> (which also strips generics).
    /// Without this, a generic type's display name "MyNS.Foo&lt;T&gt;" never equals "MyNS.Foo"
    /// and the fallback search silently fails for every generic type.
    /// </summary>
    private static string BareDisplayName(INamedTypeSymbol symbol)
    {
        var display = StripGlobalPrefix(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        var lt = display.IndexOf('<');
        return lt < 0 ? display : display[..lt];
    }

    private static string StripGlobalPrefix(string displayName)
        => displayName.StartsWith("global::", StringComparison.Ordinal)
            ? displayName["global::".Length..]
            : displayName;

    /// <summary>
    /// Cross-compilation dedup key. Same source file + same display name = same logical type
    /// (e.g. one type compiled under two target frameworks). Metadata symbols (no declaring
    /// syntax) fall back to assembly name.
    /// </summary>
    private static string DedupKey(INamedTypeSymbol type)
    {
        var display = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var file = type.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath
                   ?? type.ContainingAssembly?.Identity.Name
                   ?? "";
        return display + "|" + file;
    }

    private static string BuildAmbiguityMessage(string input, IReadOnlyList<INamedTypeSymbol> matches)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Multiple types matched '{input}' ({matches.Count} candidates). Disambiguate with --project <name>:");
        foreach (var match in matches)
        {
            var assembly = match.ContainingAssembly?.Identity.ToString() ?? "<unknown assembly>";
            var firstLocation = match.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath ?? "<no source>";
            sb.AppendLine($"  - {match.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}  [{assembly}]  {firstLocation}");
        }
        return sb.ToString();
    }
}
