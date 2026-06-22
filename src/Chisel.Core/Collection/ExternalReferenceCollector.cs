using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Collection;

public static partial class ExternalReferenceCollector
{
    // A restored NuGet package assembly lives at <globalPackages>/<id>/<version>/<asset>/... where
    // <asset> is a known package sub-folder. Keying on this layout (rather than a literal
    // ".nuget/packages") supports custom global-packages folders (NUGET_PACKAGES env var or
    // globalPackagesFolder in nuget.config, e.g. a repo-local ".nuget_cache").
    [GeneratedRegex(@"[\\/](?<id>[^\\/]+)[\\/](?<ver>[0-9][^\\/]*)[\\/](?:lib|ref|runtimes|analyzers|build|buildTransitive|contentFiles|tools|native)[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex PackageLayoutRegex();

    // .NET targeting packs (Microsoft.NETCore.App.Ref, Microsoft.AspNetCore.App.Ref, ...) live
    // under a "packs" directory and structurally look like packages (<name>/<ver>/ref/...). They
    // are supplied by the SDK, NOT NuGet, so they must not be reported as PackageReferences.
    [GeneratedRegex(@"[\\/]packs[\\/][^\\/]+\.Ref[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkPackRegex();

    // Framework ref/runtime packs are also resolved from the NuGet cache when targeting a DOWNLEVEL
    // framework with a newer SDK (e.g. building net8.0 with the .NET 10 SDK) — then they live at
    // ~/.nuget/packages/microsoft.netcore.app.ref/<ver>/ref/... with no "packs" segment. Exclude
    // them by ID so they're never emitted as a <PackageReference> (the SDK supplies them implicitly
    // via <FrameworkReference>; emitting one yields NU1213).
    [GeneratedRegex(@"^Microsoft\.(NETCore|AspNetCore|WindowsDesktop)\.App(\.(Ref|Host|Runtime).*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkPackIdRegex();

    /// <summary>
    /// Recovers (package id, version) from a resolved assembly path, or null when the path is not a
    /// NuGet package (a framework/targeting pack, a loose &lt;Reference&gt;, etc.).
    /// </summary>
    internal static (string Id, string Version)? TryParsePackageFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || FrameworkPackRegex().IsMatch(path))
        {
            return null;
        }

        var match = PackageLayoutRegex().Match(path);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups["id"].Value;
        // A framework pack resolved from the NuGet cache (downlevel TFM) is not a real package.
        return FrameworkPackIdRegex().IsMatch(id) ? null : (id, match.Groups["ver"].Value);
    }

    public static async Task<IReadOnlyList<ExternalReference>> CollectAsync(
        IEnumerable<Project> contributingProjects,
        IEnumerable<INamedTypeSymbol> externalTypes,
        DiagnosticSink diagnostics,
        CancellationToken cancellationToken = default)
    {
        // Build a (AssemblyIdentity → PortableExecutableReference) index ONLY from the projects
        // that actually contributed collected files. Indexing every project in the solution
        // would be slower and could resolve an assembly to the wrong reference path (e.g. a
        // different package TFM subfolder) used by an unrelated project.
        var refIndex = new Dictionary<AssemblyIdentity, PortableExecutableReference>();
        foreach (var project in contributingProjects.Distinct())
        {
            // A project that fails to compile shouldn't lose us ALL references — just this
            // project's contribution to the reference index.
            await diagnostics.GuardAsync("References", project.Name, async () =>
            {
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                {
                    return;
                }
                foreach (var mref in compilation.References.OfType<PortableExecutableReference>())
                {
                    var asym = compilation.GetAssemblyOrModuleSymbol(mref) as IAssemblySymbol;
                    if (asym is not null)
                    {
                        refIndex.TryAdd(asym.Identity, mref);
                    }
                }
            }).ConfigureAwait(false);
        }

        var byAssembly = new Dictionary<AssemblyIdentity, ExternalReference>();
        foreach (var type in externalTypes)
        {
            var asm = type.ContainingAssembly;
            if (asm is null || byAssembly.ContainsKey(asm.Identity))
            {
                continue;
            }

            // Resolving one reference's NuGet metadata must not drop the others.
            diagnostics.Guard("References", asm.Identity.Name, () =>
            {
                refIndex.TryGetValue(asm.Identity, out var mref);
                var path = mref?.FilePath;
                var pkg = TryParsePackageFromPath(path);

                byAssembly[asm.Identity] = new ExternalReference(
                    AssemblyName: asm.Identity.Name,
                    AssemblyVersion: asm.Identity.Version.ToString(),
                    PackageId: pkg?.Id,
                    PackageVersion: pkg?.Version,
                    ReferenceFilePath: path);
            });
        }

        return byAssembly.Values
            .OrderBy(r => r.PackageId is null ? 1 : 0)
            .ThenBy(r => r.PackageId ?? r.AssemblyName, StringComparer.Ordinal)
            .ToList();
    }
}
