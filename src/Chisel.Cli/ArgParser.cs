using Bennewitz.Ninja.Chisel;
using Bennewitz.Ninja.Chisel.Collection;

namespace Bennewitz.Ninja.Chisel.Cli;

internal static class ArgParser
{
    public static SliceOptions Parse(string[] args)
    {
        string? type = null;
        string? solution = null;
        string? output = null;
        string? project = null;
        string? tfm = null;
        var walkDepth = WalkDepth.Signatures;
        var expansion = ImplementationExpansion.SeedOnly;
        var generators = SourceGeneratorPolicy.Reference;
        var allowPartial = false;
        var restore = false;
        var excludePaths = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--type":
                case "-t":
                    type = RequireValue(args, ref i, arg);
                    break;
                case "--solution":
                case "-s":
                    solution = RequireValue(args, ref i, arg);
                    break;
                case "--output":
                case "-o":
                    output = RequireValue(args, ref i, arg);
                    break;
                case "--project":
                    project = RequireValue(args, ref i, arg);
                    break;
                case "--tfm":
                    tfm = RequireValue(args, ref i, arg);
                    break;
                case "--walk-depth":
                    walkDepth = ParseWalkDepth(RequireValue(args, ref i, arg));
                    break;
                case "--expand-impls":
                    expansion = ParseExpansion(RequireValue(args, ref i, arg));
                    break;
                case "--no-derived":
                    // Backward-compatible alias for the strictest expansion scope.
                    expansion = ImplementationExpansion.None;
                    break;
                case "--source-generators":
                    generators = ParseGeneratorPolicy(RequireValue(args, ref i, arg));
                    break;
                case "--exclude":
                case "-x":
                    // Repeatable: each occurrence adds a directory subtree to drop from collection.
                    // Resolved to an absolute path here (like --solution/--output) so the run records
                    // and matches a stable, fully-qualified path.
                    excludePaths.Add(Path.GetFullPath(RequireValue(args, ref i, arg)));
                    break;
                case "--exclude-from":
                    // Repeatable: read a file of exclusion directories (one per line) and add them all.
                    // Handy when there are too many long paths to pass as repeated --exclude flags.
                    AddExclusionsFromFile(RequireValue(args, ref i, arg), excludePaths);
                    break;
                case "--allow-partial":
                    allowPartial = true;
                    break;
                case "--restore":
                    restore = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        if (type is null) throw new ArgumentException("--type is required");
        if (solution is null) throw new ArgumentException("--solution is required");
        if (output is null) throw new ArgumentException("--output is required");

        return new SliceOptions(
            SolutionPath: Path.GetFullPath(solution),
            TypeName: type,
            OutputDirectory: Path.GetFullPath(output),
            ProjectFilter: project,
            PreferredTargetFramework: tfm,
            WalkDepth: walkDepth,
            ImplementationExpansion: expansion,
            SourceGenerators: generators,
            AllowPartial: allowPartial,
            Restore: restore,
            ExcludePaths: excludePaths.Count > 0 ? excludePaths : null);
    }

    private static string RequireValue(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {name}");
        }
        return Unquote(args[++i]);
    }

    /// <summary>
    /// Strips one layer of matched surrounding quotes from a value. Most shells strip quotes before
    /// the process sees them, but some launchers do not — notably Rider's multi-line "Program
    /// arguments" editor, which passes each line verbatim. Without this, a value typed as
    /// <c>"C:\path"</c> arrives with literal quote characters, so <see cref="Path.GetFullPath(string)"/>
    /// treats the leading quote as a relative segment and yields a garbage path that fails later.
    /// </summary>
    internal static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var quote = value[0];
            if ((quote == '"' || quote == '\'') && value[^1] == quote)
            {
                return value[1..^1];
            }
        }
        return value;
    }

    /// <summary>
    /// Adds the exclusion directories listed in <paramref name="file"/> (one path per line) to
    /// <paramref name="excludePaths"/>. The literal <c>-</c> reads from standard input, enabling the
    /// idiomatic PowerShell pipe <c>$paths | chisel --exclude-from -</c>. Blank lines and lines
    /// beginning with <c>#</c> (comments) are ignored, and one layer of surrounding quotes is stripped
    /// per line. Absolute paths are used as-is; relative paths resolve against the file's own directory
    /// (or the working directory when reading from stdin), so an exclusions file is portable alongside
    /// the project it describes. A missing or unreadable file is a usage error.
    /// </summary>
    private static void AddExclusionsFromFile(string file, List<string> excludePaths)
    {
        // "-" → read the list from stdin; relative entries resolve against the working directory.
        if (file == "-")
        {
            // Reading stdin only makes sense when something is piped in. In an interactive console
            // (e.g. running under an IDE/debugger such as Rider) stdin is not redirected and never
            // sends EOF, so a naive read would block forever with no output. Fail fast with guidance.
            if (!Console.IsInputRedirected)
            {
                throw new ArgumentException(
                    "--exclude-from - reads the exclusion list from stdin, but nothing is piped in. " +
                    "Pipe a list (e.g. $paths | chisel … --exclude-from -), or pass a file path instead. " +
                    "In an IDE/debugger (e.g. Rider) stdin is interactive — use a file path, not '-'.");
            }

            AddExclusionLines(ReadAllLines(Console.In), Directory.GetCurrentDirectory(), excludePaths);
            return;
        }

        var fullFile = Path.GetFullPath(file);
        if (!File.Exists(fullFile))
        {
            throw new ArgumentException($"--exclude-from file not found: {fullFile}");
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(fullFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"--exclude-from could not read '{fullFile}': {ex.Message}");
        }

        var baseDir = Path.GetDirectoryName(fullFile) ?? Directory.GetCurrentDirectory();
        AddExclusionLines(lines, baseDir, excludePaths);
    }

    private static void AddExclusionLines(IEnumerable<string> lines, string baseDir, List<string> excludePaths)
    {
        foreach (var rawLine in lines)
        {
            var line = Unquote(rawLine.Trim());
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            excludePaths.Add(Path.IsPathRooted(line)
                ? Path.GetFullPath(line)
                : Path.GetFullPath(Path.Combine(baseDir, line)));
        }
    }

    private static IEnumerable<string> ReadAllLines(TextReader reader)
    {
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return lines;
    }

    private static SourceGeneratorPolicy ParseGeneratorPolicy(string value) => value.ToLowerInvariant() switch
    {
        "skip" => SourceGeneratorPolicy.Skip,
        "materialize" => SourceGeneratorPolicy.Materialize,
        "reference" => SourceGeneratorPolicy.Reference,
        _ => throw new ArgumentException($"--source-generators must be skip|materialize|reference; got '{value}'"),
    };

    private static WalkDepth ParseWalkDepth(string value) => value.ToLowerInvariant() switch
    {
        "signatures" => WalkDepth.Signatures,
        "bodies" => WalkDepth.Bodies,
        _ => throw new ArgumentException($"--walk-depth must be signatures|bodies; got '{value}'"),
    };

    private static ImplementationExpansion ParseExpansion(string value) => value.ToLowerInvariant() switch
    {
        "seed" => ImplementationExpansion.SeedOnly,
        "all" => ImplementationExpansion.All,
        "none" => ImplementationExpansion.None,
        _ => throw new ArgumentException($"--expand-impls must be seed|all|none; got '{value}'"),
    };
}
