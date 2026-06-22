using Microsoft.CodeAnalysis;

namespace Bennewitz.Ninja.Chisel.Collection;

public sealed record CollectedFile(
    string AbsolutePath,
    string ProjectName,
    string ProjectFilePath,
    string TargetFramework,
    IReadOnlyList<string> ContainingSymbols,
    bool IsGenerated,
    // Generator output text to materialize (set only for IsGenerated files under the
    // Materialize policy; the AbsolutePath is synthetic and not present on disk).
    string? GeneratedText = null);

public sealed record CollectedProject(
    string Name,
    string FilePath,
    string TargetFramework,
    string Sdk,
    string? LangVersion,
    string Nullable,
    bool AllowUnsafeBlocks,
    bool ImplicitUsings,
    string? RootNamespace,
    IReadOnlyList<string> DefineConstants,
    // Numeric forms used to pick the MAX setting across a multi-project slice (a slice spanning
    // projects with different LangVersion/Nullable must compile to the strictest/highest).
    int LangVersionValue = 0,
    int NullableValue = 0);

public sealed record ExternalReference(
    string AssemblyName,
    string AssemblyVersion,
    string? PackageId,
    string? PackageVersion,
    string? ReferenceFilePath);
