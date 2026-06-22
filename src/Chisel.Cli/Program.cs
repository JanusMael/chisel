using System.Globalization;
using Bennewitz.Ninja.Chisel.Cli;
using Bennewitz.Ninja.Chisel.Workspace;

// Locale-independent output (numbers in the summary/manifest parse the same everywhere).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// --help / --version / no-args don't touch MSBuild, so answer them BEFORE the locator —
// they must work even on a machine that has no .NET SDK installed.
if (CliEntry.TryHandleInfoOnly(args, out var infoExitCode))
{
    return infoExitCode;
}

// CRITICAL: nothing above — and nothing else in Main — may reference Microsoft.Build.* /
// MSBuildWorkspace types. The CLR resolves assembly references at JIT time, and the
// Microsoft.Build.* runtime assemblies are intentionally not shipped (ExcludeAssets=runtime);
// RegisterDefaults() installs the resolver that binds them from the installed SDK, so it must run
// before any code that loads them.
//
// When no SDK is present, emit a clear, actionable message and a dedicated exit code (7) instead
// of letting the locator throw an unhandled, stack-trace-y InvalidOperationException.
if (!MsBuildBootstrapper.TryEnsureRegistered(out var sdkError))
{
    Console.Error.WriteLine($"Error: {sdkError}");
    return 7;
}

// Ctrl+C → cooperative cancellation (the run unwinds to exit code 130), not a hard kill.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await CliEntry.RunAsync(args, cts.Token).ConfigureAwait(false);
