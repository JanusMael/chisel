using Microsoft.Build.Locator;

namespace Bennewitz.Ninja.Chisel.Workspace;

public static class MsBuildBootstrapper
{
    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>
    /// The actionable message shown when no MSBuild/.NET SDK can be located. chisel drives the
    /// installed SDK's MSBuild (the <c>Microsoft.Build.*</c> assemblies are intentionally not
    /// shipped — <c>ExcludeAssets=runtime</c>), so even a self-contained build requires an SDK.
    /// </summary>
    public const string NoSdkMessage =
        "No .NET SDK was found on this machine. chisel uses MSBuild — which ships with the .NET " +
        "SDK — to load and analyze projects, so a .NET SDK must be installed even when running a " +
        "self-contained chisel binary. Install the .NET SDK (10.x) from " +
        "https://dotnet.microsoft.com/download, make sure 'dotnet' is on your PATH, then re-run.";

    /// <summary>
    /// Locates and registers an installed MSBuild (from a .NET SDK) so the workspace can load real
    /// projects. Idempotent; safe to call repeatedly. Throws <see cref="InvalidOperationException"/>
    /// (with <see cref="NoSdkMessage"/>) when no SDK/MSBuild can be found. A CLI/host that wants a
    /// clean message and exit code instead of an exception should call
    /// <see cref="TryEnsureRegistered"/>.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (!TryEnsureRegistered(out var error))
        {
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>
    /// Like <see cref="EnsureRegistered"/>, but returns <c>false</c> with an actionable
    /// <paramref name="error"/> (<see cref="NoSdkMessage"/>) instead of throwing when no .NET SDK /
    /// MSBuild instance is installed — e.g. running a self-contained build on a machine without the
    /// SDK. Idempotent.
    /// </summary>
    public static bool TryEnsureRegistered(out string? error)
    {
        lock (Gate)
        {
            if (_registered)
            {
                error = null;
                return true;
            }

            try
            {
                if (!MSBuildLocator.IsRegistered)
                {
                    MSBuildLocator.RegisterDefaults();
                }
            }
            catch (InvalidOperationException)
            {
                // The locator found no usable MSBuild. It throws "No instances of MSBuild could be
                // detected" when none are registrable, and "Path to dotnet executable is not set…"
                // when no dotnet is discoverable at all (e.g. a self-contained build on a machine
                // with no SDK). Either way there is no SDK — surface our own actionable message
                // instead of the locator's internal wording, and let the caller report it.
                error = NoSdkMessage;
                return false;
            }

            _registered = true;
            error = null;
            return true;
        }
    }
}
