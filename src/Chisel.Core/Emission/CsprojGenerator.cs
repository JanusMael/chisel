using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bennewitz.Ninja.Chisel.Collection;

namespace Bennewitz.Ninja.Chisel.Emission;

public static class CsprojGenerator
{
    /// <summary>
    /// Emits Slice.csproj at <paramref name="outputRoot"/> containing explicit &lt;Compile Include&gt; entries
    /// for every copied file plus &lt;PackageReference&gt; entries for every detected NuGet package.
    /// </summary>
    /// <param name="outputRoot">Directory where Slice.csproj will be written.</param>
    /// <param name="copiedFiles">Mapping from original .cs path to the copied path under outputRoot.</param>
    /// <param name="projects">Collected projects that contributed files (used to hoist compilation settings).</param>
    /// <param name="references">External references (NuGet packages become PackageReference items).</param>
    public static IReadOnlyList<string> Write(
        string outputRoot,
        IReadOnlyDictionary<string, string> copiedFiles,
        IReadOnlyList<CollectedProject> projects,
        IEnumerable<ExternalReference> references)
    {
        var warnings = new List<string>();

        // A slice flattens potentially-heterogeneous projects into one csproj. For each setting we
        // pick the value most likely to let every file compile, and warn when projects disagree.
        var baseline = projects.FirstOrDefault();
        var sdk = baseline?.Sdk ?? "Microsoft.NET.Sdk";
        // A Microsoft.NET.Sdk.Web vs Microsoft.NET.Sdk mismatch is semantically significant
        // (different implicit references / Razor SDK), so surface it.
        WarnIfDisagree(warnings, projects, "Sdk", p => p.Sdk);

        // RootNamespace affects files that rely on folder/implicit namespaces. Carry the baseline's
        // value through (the SDK would otherwise default it to the slice assembly name) and warn if
        // contributing projects disagree — a single flattened value can't satisfy all of them.
        var rootNamespace = baseline?.RootNamespace;
        WarnIfDisagree(warnings, projects, "RootNamespace", p => p.RootNamespace ?? "(default)");

        // LangVersion: take the HIGHEST — files written for a newer language version won't compile
        // under an older one, but newer-version source generally tolerates older syntax.
        var langProject = projects.OrderByDescending(p => p.LangVersionValue).FirstOrDefault();
        var langVersion = langProject?.LangVersion ?? "latest";
        WarnIfDisagree(warnings, projects, "LangVersion", p => p.LangVersion ?? "(default)");

        // Nullable: take the STRICTEST (Enable > Annotations > Warnings > Disable) so nullable
        // annotations in any file remain valid (avoids CS8632).
        var nullProject = projects.OrderByDescending(p => p.NullableValue).FirstOrDefault();
        var nullable = MapNullable(nullProject?.Nullable);
        WarnIfDisagree(warnings, projects, "Nullable", p => p.Nullable);

        var targetFramework = baseline?.TargetFramework ?? "net10.0";
        WarnIfDisagree(warnings, projects, "TargetFramework", p => p.TargetFramework);

        var allowUnsafe = projects.Any(p => p.AllowUnsafeBlocks);
        // ImplicitUsings: enable if ANY project uses it — files from that project rely on the
        // implicit global usings, and a file from a non-implicit project is unaffected by extra
        // usings being in scope. (Using All() would break the common implicit-usings project.)
        var implicitUsings = projects.Any(p => p.ImplicitUsings);
        WarnIfDisagree(warnings, projects, "ImplicitUsings", p => p.ImplicitUsings ? "enable" : "disable");
        // Emit only user-defined constants. The SDK re-injects DEBUG/TRACE and all the
        // framework/platform monikers (NET10_0, NET10_0_OR_GREATER, NETCOREAPP, ...) at build
        // time; re-emitting them here would pin the slice to DEBUG and to one TFM's guards.
        var defineConstants = projects
            .SelectMany(p => p.DefineConstants)
            .Where(IsUserDefinedConstant)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var project = new XElement("Project",
            new XAttribute("Sdk", sdk),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", targetFramework),
                new XElement("LangVersion", langVersion),
                new XElement("Nullable", nullable),
                new XElement("ImplicitUsings", implicitUsings ? "enable" : "disable"),
                string.IsNullOrWhiteSpace(rootNamespace) ? null : new XElement("RootNamespace", rootNamespace),
                // Explicit <Compile Include> below is the source of truth for the slice —
                // disable the SDK's default **/*.cs glob to avoid CS2002 (specified twice)
                // and to prevent stray files in the output dir from being pulled in.
                new XElement("EnableDefaultCompileItems", "false"),
                allowUnsafe ? new XElement("AllowUnsafeBlocks", "true") : null,
                defineConstants.Count > 0 ? new XElement("DefineConstants", string.Join(";", defineConstants)) : null));

        var compileGroup = new XElement("ItemGroup");
        foreach (var pair in copiedFiles.OrderBy(p => p.Value, StringComparer.Ordinal))
        {
            var includePath = Path.GetRelativePath(outputRoot, pair.Value).Replace('\\', '/');
            compileGroup.Add(new XElement("Compile", new XAttribute("Include", includePath)));
        }
        project.Add(compileGroup);

        var packages = references
            .Where(r => r.PackageId is not null)
            .GroupBy(r => r.PackageId!, StringComparer.OrdinalIgnoreCase)
            // Pick the highest SEMANTIC version, not the lexicographically-largest string
            // ("10.0.0" must beat "9.0.0", which string ordering gets wrong).
            .Select(g => g.OrderByDescending(x => ParseVersion(x.PackageVersion)).First())
            .OrderBy(r => r.PackageId, StringComparer.Ordinal)
            .ToList();

        if (packages.Count > 0)
        {
            var pkgGroup = new XElement("ItemGroup");
            foreach (var pkg in packages)
            {
                pkgGroup.Add(new XElement("PackageReference",
                    new XAttribute("Include", pkg.PackageId!),
                    new XAttribute("Version", pkg.PackageVersion ?? "*")));
            }
            project.Add(pkgGroup);
        }

        var doc = new XDocument(project);
        var destination = Path.Combine(outputRoot, "Slice.csproj");
        Directory.CreateDirectory(outputRoot);
        var settings = new System.Xml.XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using (var writer = System.Xml.XmlWriter.Create(destination, settings))
        {
            doc.Save(writer);
        }

        return warnings;
    }

    private static void WarnIfDisagree(
        List<string> warnings,
        IReadOnlyList<CollectedProject> projects,
        string setting,
        Func<CollectedProject, string> selector)
    {
        var distinct = projects.Select(selector).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count > 1)
        {
            warnings.Add(
                $"Contributing projects disagree on {setting} ({string.Join(", ", distinct)}); " +
                $"the slice uses the highest/most-permissive value. Verify the slice compiles.");
        }
    }

    private static string MapNullable(string? roslynValue) => roslynValue switch
    {
        "Enable" => "enable",
        "Annotations" => "annotations",
        "Warnings" => "warnings",
        "Disable" => "disable",
        null => "disable",
        _ => roslynValue.ToLowerInvariant(),
    };

    private static readonly Regex SdkInjectedConstant = new(
        @"^(DEBUG|TRACE|RELEASE|NET|NETCOREAPP|NETSTANDARD|NETFRAMEWORK|WINDOWS|LINUX|MACOS|OSX|ANDROID|IOS|BROWSER)([0-9].*)?(_OR_GREATER)?$",
        RegexOptions.Compiled);

    private static bool IsUserDefinedConstant(string symbol)
        => !SdkInjectedConstant.IsMatch(symbol);

    private static Version ParseVersion(string? version)
    {
        if (version is null)
        {
            return new Version(0, 0);
        }
        // Strip any pre-release/build suffix (e.g. "13.0.3-beta1" → "13.0.3") before parsing.
        var core = version.Split('-', '+')[0];
        return Version.TryParse(core, out var v) ? v : new Version(0, 0);
    }
}
