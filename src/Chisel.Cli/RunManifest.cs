using System.Text.Json;
using System.Text.Json.Serialization;
using Bennewitz.Ninja.Chisel.Collection;
using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Cli;

/// <summary>
/// The machine-readable record of a run. Written to &lt;output&gt;/result.json on every run (success
/// or fatal) and to stdout under <c>--format json</c>. Stable, camelCase, deserialization-friendly
/// (e.g. <c>[System.Text.Json.JsonDocument]::Parse([System.IO.File]::ReadAllText("result.json"))</c>
/// from PowerShell). Bump <see cref="CurrentSchemaVersion"/> on breaking shape changes.
/// </summary>
internal sealed record RunManifest(
    int SchemaVersion,
    RunManifest.ToolInfo Tool,
    bool Success,
    int ExitCode,
    double ElapsedSeconds,
    RunManifest.SeedInfo? Seed,
    RunManifest.ModeInfo? Mode,
    string Solution,
    string Output,
    RunManifest.CountsInfo? Counts,
    RunManifest.OutputsInfo? Outputs,
    IReadOnlyList<RunManifest.PackageInfo> Packages,
    IReadOnlyList<RunManifest.DiagnosticInfo> Diagnostics,
    RunManifest.ErrorInfo? Error)
{
    public const int CurrentSchemaVersion = 1;
    public const string ToolName = "chisel";

    internal sealed record ToolInfo(string Name, string Version);
    internal sealed record SeedInfo(string DisplayName, string? FilePath);
    internal sealed record ModeInfo(string WalkDepth, string Expansion, string SourceGenerators);
    internal sealed record CountsInfo(int InSourceTypes, int Files, int Projects, int ExternalReferences, int Packages);
    internal sealed record OutputsInfo(string FileList, string References, string Csproj, string CopiedSourceRoot, string? Gitignore, string Log);
    internal sealed record PackageInfo(string Id, string? Version);
    internal sealed record DiagnosticInfo(string Severity, string Stage, string Message, string? Item);
    internal sealed record ErrorInfo(string Kind, string Message);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RunManifest Create(
        SliceOptions options, SliceResult result, TimeSpan elapsed, int exitCode, string version, string logPath)
    {
        var packages = result.ExternalReferences
            .Where(r => r.PackageId is not null)
            .Select(r => new PackageInfo(r.PackageId!, r.PackageVersion))
            .ToList();

        return new RunManifest(
            SchemaVersion: CurrentSchemaVersion,
            Tool: new ToolInfo(ToolName, version),
            Success: exitCode == 0,
            ExitCode: exitCode,
            ElapsedSeconds: Math.Round(elapsed.TotalSeconds, 3),
            Seed: new SeedInfo(result.SeedTypeDisplay, result.SeedFilePath),
            Mode: new ModeInfo(MapWalkDepth(options.WalkDepth), MapExpansion(options.ImplementationExpansion), MapSourceGenerators(options.SourceGenerators)),
            Solution: options.SolutionPath,
            Output: options.OutputDirectory,
            Counts: new CountsInfo(result.InSourceTypeCount, result.Files.Count, result.Projects.Count, result.ExternalReferences.Count, packages.Count),
            Outputs: new OutputsInfo(result.FileListPath, result.ReferenceManifestPath, result.CsprojPath, result.CopiedSourceRoot, result.GitignorePath, logPath),
            Packages: packages,
            Diagnostics: result.Diagnostics.Select(ToDiagnosticInfo).ToList(),
            Error: null);
    }

    public static RunManifest CreateFailure(
        SliceOptions options, string version, int exitCode, string logPath, string errorKind, string errorMessage)
        => new(
            SchemaVersion: CurrentSchemaVersion,
            Tool: new ToolInfo(ToolName, version),
            Success: false,
            ExitCode: exitCode,
            ElapsedSeconds: 0,
            Seed: new SeedInfo(options.TypeName, null),
            Mode: new ModeInfo(MapWalkDepth(options.WalkDepth), MapExpansion(options.ImplementationExpansion), MapSourceGenerators(options.SourceGenerators)),
            Solution: options.SolutionPath,
            Output: options.OutputDirectory,
            Counts: null,
            Outputs: new OutputsInfo("", "", "", "", null, logPath),
            Packages: Array.Empty<PackageInfo>(),
            Diagnostics: Array.Empty<DiagnosticInfo>(),
            Error: new ErrorInfo(errorKind, errorMessage));

    private static DiagnosticInfo ToDiagnosticInfo(SliceDiagnostic d)
        => new(d.Severity.ToString(), d.Stage, d.Message, d.Item);

    // Match the CLI flag spellings so the manifest reads the same as the command that produced it.
    private static string MapWalkDepth(WalkDepth depth) => depth switch
    {
        WalkDepth.Signatures => "signatures",
        WalkDepth.Bodies => "bodies",
        _ => depth.ToString().ToLowerInvariant(),
    };

    private static string MapExpansion(ImplementationExpansion expansion) => expansion switch
    {
        ImplementationExpansion.SeedOnly => "seed",
        ImplementationExpansion.All => "all",
        ImplementationExpansion.None => "none",
        _ => expansion.ToString().ToLowerInvariant(),
    };

    private static string MapSourceGenerators(SourceGeneratorPolicy policy) => policy switch
    {
        SourceGeneratorPolicy.Reference => "reference",
        SourceGeneratorPolicy.Materialize => "materialize",
        SourceGeneratorPolicy.Skip => "skip",
        _ => policy.ToString().ToLowerInvariant(),
    };
}
