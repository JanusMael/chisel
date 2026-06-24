using System.Diagnostics;
using System.Reflection;
using Bennewitz.Ninja.Chisel;
using Bennewitz.Ninja.Chisel.Diagnostics;
using Bennewitz.Ninja.Chisel.Resolution;
using Bennewitz.Ninja.Chisel.Workspace;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using Serilog.Sinks.SystemConsole.Themes;

namespace Bennewitz.Ninja.Chisel.Cli;

internal static class CliEntry
{
    private const string LogFileName = "chisel.log";
    private const string ResultJsonName = "result.json";

    internal enum OutputFormat { Text, Json }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (TryHandleInfoOnly(args, out var infoExitCode))
        {
            return infoExitCode;
        }

        var version = ToolVersion();

        // Presentation-only flags handled here (Core's SliceOptions doesn't know them); ArgParser
        // throws on anything it doesn't recognize, so strip them — including --format's value.
        var verbose = false;
        var quiet = false;
        var strict = false;
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        var format = OutputFormat.Text;
        var coreArgs = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--verbose" or "-v": verbose = true; break;
                case "--quiet" or "-q": quiet = true; break;
                case "--strict": strict = true; break;
                case "--no-color": noColor = true; break;
                case "--format":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: --format requires a value (text|json)");
                        PrintUsage();
                        return 2;
                    }
                    var value = args[++i];
                    switch (value.ToLowerInvariant())
                    {
                        case "text": format = OutputFormat.Text; break;
                        case "json": format = OutputFormat.Json; break;
                        default:
                            Console.Error.WriteLine($"Error: --format must be text|json; got '{value}'");
                            PrintUsage();
                            return 2;
                    }
                    break;
                default: coreArgs.Add(args[i]); break;
            }
        }

        SliceOptions options;
        try
        {
            options = ArgParser.Parse(coreArgs.ToArray());
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            PrintUsage();
            return 2;
        }

        // Narrative (header/progress/diagnostics) → stderr + run-log file. The result payload (text
        // summary or JSON) → stdout. The run log (reset each run) always captures everything.
        // This runs before the logger exists, so a bad output path must fail with a clear message
        // rather than an unhandled crash with no output.
        try
        {
            Directory.CreateDirectory(options.OutputDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Error: cannot create output directory '{options.OutputDirectory}': {ex.Message}");
            Console.Error.WriteLine("If you wrapped paths in quotes, remove them — some launchers (e.g. Rider's multi-line args editor) pass quotes literally.");
            return 2;
        }
        var logPath = Path.Combine(options.OutputDirectory, LogFileName);
        ResetLogFile(logPath);
        Log.Logger = BuildLogger(logPath, quiet, noColor);

        var stopwatch = Stopwatch.StartNew();
        var progressGate = new object();
        var currentLabel = "starting up";
        var lastPrintedAt = TimeSpan.Zero;

        void OnProgress(SliceProgress progress)
        {
            lock (progressGate)
            {
                currentLabel = progress.Message;
                if (progress.Kind == ProgressKind.Phase)
                {
                    lastPrintedAt = stopwatch.Elapsed;
                }
            }

            if (progress.Kind == ProgressKind.Phase)
            {
                Info($"[{stopwatch.Elapsed:mm\\:ss}] {progress.Message}");
            }
        }

        int Fatal(int code, string kind, string message)
        {
            Log.Error("{Line}", message);
            var manifest = RunManifest.CreateFailure(options, version, code, logPath, kind, message);
            WriteResultJson(options.OutputDirectory, manifest);
            if (format == OutputFormat.Json)
            {
                Console.Out.WriteLine(manifest.ToJson());
            }
            return code;
        }

        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = RunHeartbeatAsync(stopwatch, () =>
        {
            lock (progressGate)
            {
                return (currentLabel, lastPrintedAt);
            }
        }, heartbeatCts.Token);

        try
        {
            Info($"{RunManifest.ToolName} {version}");
            Info($"Slicing {options.TypeName}");
            Info($"  from {options.SolutionPath}");
            Info($"  into {options.OutputDirectory}  (mode: {WalkDepthLabel(options.WalkDepth)} depth, {ExpansionLabel(options.ImplementationExpansion)} expansion)");
            Info($"  log  {logPath}");
            Info("");

            var result = await SliceRunner.RunAsync(options, ReportLive, OnProgress, cancellationToken).ConfigureAwait(false);
            heartbeatCts.Cancel();

            var exitCode = ResolveExitCode(result, strict);
            var manifest = RunManifest.Create(options, result, stopwatch.Elapsed, exitCode, version, logPath);
            WriteResultJson(options.OutputDirectory, manifest);

            if (format == OutputFormat.Json)
            {
                Console.Out.WriteLine(manifest.ToJson());
                Info("Result emitted as JSON to stdout and result.json.");
            }
            else
            {
                WriteTextSummary(options, result, stopwatch.Elapsed, logPath, verbose);
            }

            return exitCode;
        }
        catch (OperationCanceledException)
        {
            heartbeatCts.Cancel();
            return Fatal(130, "Canceled", "Canceled.");
        }
        catch (TypeResolutionException ex)
        {
            heartbeatCts.Cancel();
            return Fatal(3, "TypeResolution", ex.Message);
        }
        catch (WorkspaceLoadException ex)
        {
            heartbeatCts.Cancel();
            return Fatal(4, "WorkspaceLoad", ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            heartbeatCts.Cancel();
            return Fatal(5, "FileNotFound", ex.Message);
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch
            {
                // Heartbeat is best-effort; never let it affect the exit path.
            }

            Log.CloseAndFlush();
        }
    }

    /// <summary>Default exit is 0 when a slice is produced; --strict promotes any error diagnostic to exit 6.</summary>
    internal static int ResolveExitCode(SliceResult result, bool strict)
        => strict && result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? 6 : 0;

    private static void WriteTextSummary(SliceOptions options, SliceResult result, TimeSpan elapsed, string logPath, bool verbose)
    {
        var packageCount = result.ExternalReferences.Count(r => r.PackageId is not null);
        var errorCount = result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warningCount = result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        // Diagnostics detail goes FIRST and the artifact paths LAST: after a long run the most useful
        // thing to land on screen is where the outputs are, with the (often long) diagnostics list
        // scrolled up above the stats. The stats block carries just the error/warning counts.
        PrintDiagnosticsSummary(result.Diagnostics, verbose);

        ResultLine("");
        ResultLine($"Seed type:     {result.SeedTypeDisplay}");
        if (result.SeedFilePath is not null)
        {
            ResultLine($"  declared in: {result.SeedFilePath}");
        }
        ResultLine($"Mode:          {WalkDepthLabel(options.WalkDepth)} depth, {ExpansionLabel(options.ImplementationExpansion)} expansion");
        ResultLine($"Types walked:  {result.InSourceTypeCount} in-source");
        ResultLine($"Files:         {result.Files.Count}");
        ResultLine($"External refs: {result.ExternalReferences.Count} ({packageCount} NuGet packages)");
        ResultLine($"Projects:      {result.Projects.Count}");
        ResultLine($"Elapsed:       {elapsed:mm\\:ss}");
        if (errorCount > 0)
        {
            ResultLine($"Errors:        {errorCount}  (detail above)");
        }
        if (warningCount > 0)
        {
            ResultLine($"Warnings:      {warningCount}  (detail above)");
        }

        PrintNextSteps(options, result);

        ResultLine("");
        ResultLine($"To build the slice:  dotnet build \"{result.CsprojPath}\"");

        ResultLine("");
        ResultLine($"  files.json       → {result.FileListPath}");
        ResultLine($"  references.json  → {result.ReferenceManifestPath}");
        ResultLine($"  result.json      → {Path.Combine(options.OutputDirectory, ResultJsonName)}");
        ResultLine($"  Slice.csproj     → {result.CsprojPath}");
        ResultLine($"  copied sources   → {result.CopiedSourceRoot}");
        if (result.GitignorePath is not null)
        {
            ResultLine($"  .gitignore       → {result.GitignorePath}");
        }
        ResultLine($"  run log          → {logPath}");
    }

    private static ILogger BuildLogger(string logPath, bool quiet, bool noColor)
    {
        var consoleLevel = quiet ? LogEventLevel.Warning : LogEventLevel.Information;
        var theme = noColor ? AnsiConsoleTheme.None : AnsiConsoleTheme.Literate;

        var config = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            // Console → stderr, excluding the "Result"-tagged summary (that goes to stdout). The
            // exclusion is on this sub-logger only, so the file sink below still records everything.
            .WriteTo.Logger(lc => lc
                .Filter.ByExcluding(Matching.WithProperty("Result"))
                .WriteTo.Console(
                    standardErrorFromLevel: LogEventLevel.Verbose,
                    restrictedToMinimumLevel: consoleLevel,
                    theme: theme,
                    outputTemplate: "{Message:lj}{NewLine}{Exception}"));

        try
        {
            config = config.WriteTo.File(
                logPath,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        }
        catch
        {
            // Console-only fallback.
        }

        return config.CreateLogger();
    }

    private static void ResetLogFile(string logPath)
    {
        try
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
        catch
        {
            // If we can't delete it, Serilog will append; not worth failing the run over.
        }
    }

    private static void WriteResultJson(string outputDir, RunManifest manifest)
    {
        try
        {
            File.WriteAllText(Path.Combine(outputDir, ResultJsonName), manifest.ToJson());
        }
        catch (Exception ex)
        {
            Log.Warning("{Line}", $"Could not write {ResultJsonName}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string ToolVersion()
    {
        var assembly = typeof(CliEntry).Assembly;

        // Bennewitz.Ninja.AutoVersioning stamps the CalVer (YEAR.QUARTER.MMDD.HHmm) as the file/
        // assembly version and — when a PublicVersion is supplied (the release tag, in CI) — records
        // it as an AssemblyMetadata("PublicVersion", …) entry. It puts a commit string (e.g.
        // "Commit♥: <sha>"), NOT a version, in AssemblyInformationalVersion, so we never read that.
        // Prefer the public/release version; otherwise fall back to the build's CalVer file version.
        var publicVersion = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "PublicVersion", StringComparison.Ordinal))?.Value;
        if (!string.IsNullOrWhiteSpace(publicVersion))
        {
            return publicVersion;
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "dev";
    }

    // Narrative → stderr (+ file). Result → stdout (+ file, tagged so the console sub-logger skips it).
    private static void Info(string line) => Log.Information("{Line}", line);

    private static void ResultLine(string line)
    {
        Console.Out.WriteLine(line);
        Log.ForContext("Result", true).Information("{Line}", line);
    }

    private static async Task RunHeartbeatAsync(
        Stopwatch stopwatch,
        Func<(string Phase, TimeSpan LastMessageAt)> snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var (phase, lastMessageAt) = snapshot();
                if (stopwatch.Elapsed - lastMessageAt >= TimeSpan.FromSeconds(4))
                {
                    Info($"[{stopwatch.Elapsed:mm\\:ss}] … still working — {phase}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private static void ReportLive(SliceDiagnostic diagnostic)
    {
        if (diagnostic.Severity == DiagnosticSeverity.Error)
        {
            Log.Error("{Line}", "  " + diagnostic.Format());
        }
        else
        {
            Log.Warning("{Line}", "  " + diagnostic.Format());
        }
    }

    private static void PrintDiagnosticsSummary(IReadOnlyList<SliceDiagnostic> diagnostics, bool verbose)
    {
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();
        if (errors.Count == 0 && warnings.Count == 0)
        {
            return;
        }

        ResultLine("");
        ResultLine($"Diagnostics: {errors.Count} error(s), {warnings.Count} warning(s) — the slice was still produced.");

        if (verbose)
        {
            foreach (var d in errors.Concat(warnings))
            {
                ResultLine("  " + d.Format());
            }
            return;
        }

        foreach (var e in errors)
        {
            ResultLine("  " + e.Format());
        }

        foreach (var group in warnings.GroupBy(w => w.Stage).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var sample = group.First();
            var detail = sample.Item is null ? sample.Message : $"{sample.Message} — {sample.Item}";
            ResultLine($"  {group.Key}: {group.Count()} warning(s) — e.g. {detail}");
        }

        if (warnings.Count > 0)
        {
            ResultLine("  (pass --verbose to list every warning; full detail is in the run log)");
        }
    }

    private static void PrintNextSteps(SliceOptions options, SliceResult result)
    {
        var hints = new List<string>();

        if (result.Diagnostics.Any(d => d.Stage == "SourceGenerators"))
        {
            hints.Add("Some projects use source generators whose output was skipped — re-run with --source-generators materialize for a self-compilable slice.");
        }

        if (options.WalkDepth == WalkDepth.Signatures)
        {
            hints.Add("This is a contract/shape slice (--walk-depth signatures); it is not guaranteed to compile standalone. Use --walk-depth bodies for a self-compilable slice.");
        }

        if (hints.Count == 0)
        {
            return;
        }

        ResultLine("");
        ResultLine("Next steps:");
        foreach (var hint in hints)
        {
            ResultLine("  • " + hint);
        }
    }

    private static string WalkDepthLabel(WalkDepth depth) => depth.ToString().ToLowerInvariant();

    private static string ExpansionLabel(ImplementationExpansion expansion) => expansion switch
    {
        ImplementationExpansion.SeedOnly => "seed-only",
        ImplementationExpansion.All => "all",
        ImplementationExpansion.None => "none",
        _ => expansion.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Handles the "info only" invocations that need neither a solution nor MSBuild: no args
    /// (usage, exit 1), <c>--help</c>/<c>-h</c> (usage, exit 0), and <c>--version</c>/<c>-V</c>
    /// (exit 0). Returns true (with <paramref name="exitCode"/> set) when it handled the args.
    /// Called from <c>Program</c> <em>before</em> MSBuild registration — so these work without a
    /// .NET SDK installed — and again at the top of <see cref="RunAsync"/> for direct/embedded callers.
    /// </summary>
    internal static bool TryHandleInfoOnly(string[] args, out int exitCode)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            exitCode = args.Length == 0 ? 1 : 0;
            return true;
        }

        if (args.Contains("--version") || args.Contains("-V"))
        {
            Console.Out.WriteLine($"{RunManifest.ToolName} {ToolVersion()}");
            exitCode = 0;
            return true;
        }

        exitCode = 0;
        return false;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            chisel — collect the C# files needed to compile a given type, across a solution

            Usage:
              dotnet chisel --type <FQN> --solution <path> --output <dir> [options]

            Required:
              --type, -t <FQN>          Fully-qualified type name (e.g. MyNS.IFoo or MyNS.Repository<T>)
              --solution, -s <path>     Path to the .sln / .slnx file
              --output, -o <dir>        Output directory (created if missing)

            Options:
              --project <name>          Disambiguate when the FQN matches multiple projects
              --tfm <name>              Preferred target framework when projects multi-target
              --walk-depth <d>          signatures | bodies (default: signatures)
              --expand-impls <s>        seed | all | none (default: seed)
              --no-derived              Alias for --expand-impls none
              --source-generators <p>   skip | materialize | reference (default: reference)
              --exclude, -x <path>      Drop a directory subtree from the slice (repeatable)
              --exclude-from <file>     Read --exclude paths from a file; '-' reads stdin (repeatable)
              --restore                 Run 'dotnet restore' on the solution first
              --allow-partial           Continue when MSBuild reports project-load failures
              --format <f>              text | json (default: text)
              --strict                  Exit 6 if any error-severity diagnostic occurred
              --verbose, -v             List every diagnostic instead of grouping by stage
              --quiet, -q               Console shows only warnings/errors (full log still written)
              --no-color                Disable ANSI color (also honored via NO_COLOR)
              --version, -V             Print version and exit
              -h, --help                Show this help

            PowerShell: single-quote generic type names, e.g. 'MyNS.Repository<T>'
            Streams:    diagnostics/progress → stderr, result → stdout (text or --format json)
            Artifacts:  always writes <output>/chisel.log (run log) and <output>/result.json (manifest)

            Exit codes: 0 ok · 1 no args · 2 bad args · 3 type unresolved/ambiguous · 4 workspace load failed
                        5 solution not found · 6 errors (--strict) · 7 no .NET SDK · 130 canceled
            """);

        Console.WriteLine();
    }
}
