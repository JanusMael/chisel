using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Collection;

/// <summary>
/// Collects per-project metadata (resolved compilation settings, SDK choice) needed to
/// emit a self-contained slice csproj.
/// </summary>
public static class ProjectFileCollector
{
    public static async Task<IReadOnlyList<CollectedProject>> CollectAsync(
        IEnumerable<Project> projects,
        DiagnosticSink diagnostics,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CollectedProject>();
        var seen = new HashSet<string>(PathComparison.Comparer);

        foreach (var project in projects)
        {
            if (project.FilePath is null || !seen.Add(project.FilePath))
            {
                continue;
            }

            // Failing to read one contributing project's settings must not abort the slice — we
            // still emit a csproj using the other projects' settings (and defaults).
            await diagnostics.GuardAsync("Project", project.Name, async () =>
            {
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) as CSharpCompilation;
                if (compilation is null)
                {
                    diagnostics.Warn("Project", "No C# compilation available; project settings omitted from the slice csproj.", project.Name);
                    return;
                }

                var parse = (CSharpParseOptions)project.ParseOptions!;
                var opts = compilation.Options;

                var (sdk, rootNamespace, implicitUsings) = ReadCsprojBits(project.FilePath!, diagnostics);

                result.Add(new CollectedProject(
                    Name: project.Name,
                    FilePath: project.FilePath!,
                    TargetFramework: ExtractTargetFramework(compilation) ?? "net10.0",
                    Sdk: sdk,
                    LangVersion: parse.LanguageVersion.ToDisplayString(),
                    Nullable: opts.NullableContextOptions.ToString(),
                    AllowUnsafeBlocks: opts.AllowUnsafe,
                    ImplicitUsings: implicitUsings,
                    RootNamespace: rootNamespace,
                    DefineConstants: parse.PreprocessorSymbolNames.ToList(),
                    LangVersionValue: (int)parse.LanguageVersion,
                    NullableValue: (int)opts.NullableContextOptions));
            }).ConfigureAwait(false);
        }

        return result;
    }

    private static (string Sdk, string? RootNamespace, bool ImplicitUsings) ReadCsprojBits(string csprojPath, DiagnosticSink diagnostics)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var sdk = doc.Root?.Attribute("Sdk")?.Value ?? "Microsoft.NET.Sdk";
            var rootNs = doc.Descendants("RootNamespace").FirstOrDefault()?.Value;
            var implicitUsingsStr = doc.Descendants("ImplicitUsings").FirstOrDefault()?.Value;
            // MSBuild accepts both "enable" and "true" to turn implicit usings on.
            var implicitUsings = string.Equals(implicitUsingsStr, "enable", StringComparison.OrdinalIgnoreCase)
                || string.Equals(implicitUsingsStr, "true", StringComparison.OrdinalIgnoreCase);
            return (sdk, rootNs, implicitUsings);
        }
        catch (Exception ex)
        {
            // Don't fail — fall back to SDK defaults, but tell the user the csproj couldn't be read
            // (the slice may need a non-default SDK / RootNamespace it now lacks).
            diagnostics.Warn("Project", $"Could not read project file ({ex.GetType().Name}); using Microsoft.NET.Sdk defaults.", csprojPath);
            return ("Microsoft.NET.Sdk", null, false);
        }
    }

    private static string? ExtractTargetFramework(CSharpCompilation compilation)
    {
        // Look for [assembly: TargetFramework(".NETCoreApp,Version=v10.0")] and map.
        foreach (var attr in compilation.Assembly.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == "System.Runtime.Versioning.TargetFrameworkAttribute" &&
                attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is string frameworkName)
            {
                return MapFrameworkNameToTfm(frameworkName);
            }
        }
        return null;
    }

    private static string MapFrameworkNameToTfm(string frameworkName)
    {
        // ".NETCoreApp,Version=v10.0" → "net10.0"
        // ".NETStandard,Version=v2.1" → "netstandard2.1"
        // ".NETFramework,Version=v4.8" → "net48"
        // TrimEntries: some MSBuild targets emit ".NETFramework, Version=v4.8" with a space.
        var parts = frameworkName.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return frameworkName;
        }
        var ident = parts[0];
        var version = parts[1].Replace("Version=v", "");
        return ident switch
        {
            ".NETCoreApp" => "net" + version,
            ".NETStandard" => "netstandard" + version,
            ".NETFramework" => "net" + version.Replace(".", ""),
            _ => frameworkName,
        };
    }
}
