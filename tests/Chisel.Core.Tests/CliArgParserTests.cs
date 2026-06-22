using FluentAssertions;
using Bennewitz.Ninja.Chisel.Cli;
using Bennewitz.Ninja.Chisel;
using Bennewitz.Ninja.Chisel.Collection;

namespace Bennewitz.Ninja.Chisel.Tests;

/// <summary>
/// Unit tests for the CLI argument boundary — the entire input surface that was previously
/// untested. Pure parsing (no MSBuild), so no [Collection("MSBuild")] needed.
/// </summary>
public sealed class CliArgParserTests
{
    [Fact]
    public void Parse_MapsAllFields_AndRootsPaths()
    {
        var options = ArgParser.Parse([
            "--type", "MyNS.IFoo",
            "--solution", "some.sln",
            "--output", "out",
            "--project", "MyProj",
            "--tfm", "net8.0",
            "--walk-depth", "bodies",
            "--no-derived",
            "--source-generators", "materialize",
            "--allow-partial",
            "--restore",
        ]);

        options.TypeName.Should().Be("MyNS.IFoo", "the type name is passed through verbatim");
        options.ProjectFilter.Should().Be("MyProj");
        options.PreferredTargetFramework.Should().Be("net8.0");
        options.WalkDepth.Should().Be(WalkDepth.Bodies);
        options.ImplementationExpansion.Should().Be(ImplementationExpansion.None, "--no-derived is an alias for --expand-impls none");
        options.SourceGenerators.Should().Be(SourceGeneratorPolicy.Materialize);
        options.AllowPartial.Should().BeTrue();
        options.Restore.Should().BeTrue();
        Path.IsPathRooted(options.SolutionPath).Should().BeTrue("solution path is normalized to absolute");
        Path.IsPathRooted(options.OutputDirectory).Should().BeTrue("output path is normalized to absolute");
        options.SolutionPath.Should().EndWith("some.sln");
    }

    [Fact]
    public void Parse_SupportsShortAliases()
    {
        var options = ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o"]);
        options.TypeName.Should().Be("N.T");
        options.SolutionPath.Should().EndWith("a.sln");
        options.WalkDepth.Should().Be(WalkDepth.Signatures, "signatures-only is the default walk depth");
        options.ImplementationExpansion.Should().Be(ImplementationExpansion.SeedOnly, "seed-only is the default expansion scope");
        options.SourceGenerators.Should().Be(SourceGeneratorPolicy.Reference, "reference is the default policy");
        options.AllowPartial.Should().BeFalse("partial loading is off by default");
        options.Restore.Should().BeFalse("restore is off by default");
    }

    [Fact]
    public void Parse_StripsMatchedSurroundingQuotes_FromValues()
    {
        // Repro for the Rider multi-line-args crash: each line is passed verbatim, so values arrive
        // with literal surrounding quotes. Those must be stripped, or Path.GetFullPath("\"C:\\x\"")
        // yields a garbage path that crashes Directory.CreateDirectory before any output.
        var options = ArgParser.Parse([
            "--type", "\"Allos.Core.IApplicationContext\"",
            "--solution", "\"a.sln\"",
            "--output", "\"o\"",
            "-x", "\"vendor\"",
        ]);

        options.TypeName.Should().Be("Allos.Core.IApplicationContext", "surrounding quotes are stripped from the type");
        options.SolutionPath.Should().EndWith("a.sln").And.NotContain("\"");
        options.OutputDirectory.Should().EndWith("o").And.NotContain("\"");
        options.ExcludePaths.Should().ContainSingle().Which.Should().NotContain("\"");
    }

    [Theory]
    [InlineData("\"quoted\"", "quoted")]
    [InlineData("'quoted'", "quoted")]
    [InlineData("plain", "plain")]
    [InlineData("\"unbalanced", "\"unbalanced")]      // a lone leading quote is left intact
    [InlineData("\"", "\"")]                            // a single quote char is not a matched pair
    [InlineData("\"a\"b\"", "a\"b")]                    // only ONE layer is stripped
    public void Unquote_StripsOnlyMatchedSurroundingPair(string input, string expected)
    {
        ArgParser.Unquote(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("signatures", WalkDepth.Signatures)]
    [InlineData("bodies", WalkDepth.Bodies)]
    [InlineData("BODIES", WalkDepth.Bodies)]
    public void Parse_WalkDepth_IsCaseInsensitive(string value, WalkDepth expected)
    {
        var options = ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o", "--walk-depth", value]);
        options.WalkDepth.Should().Be(expected);
    }

    [Theory]
    [InlineData("seed", ImplementationExpansion.SeedOnly)]
    [InlineData("all", ImplementationExpansion.All)]
    [InlineData("none", ImplementationExpansion.None)]
    public void Parse_ExpandImpls_IsCaseInsensitive(string value, ImplementationExpansion expected)
    {
        var options = ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o", "--expand-impls", value]);
        options.ImplementationExpansion.Should().Be(expected);
    }

    [Fact]
    public void Parse_InvalidWalkDepth_Throws()
    {
        var act = () => ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o", "--walk-depth", "bogus"]);
        act.Should().Throw<ArgumentException>().WithMessage("*signatures|bodies*");
    }

    [Theory]
    [InlineData("skip", SourceGeneratorPolicy.Skip)]
    [InlineData("materialize", SourceGeneratorPolicy.Materialize)]
    [InlineData("reference", SourceGeneratorPolicy.Reference)]
    [InlineData("REFERENCE", SourceGeneratorPolicy.Reference)]
    public void Parse_GeneratorPolicy_IsCaseInsensitive(string value, SourceGeneratorPolicy expected)
    {
        var options = ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o", "--source-generators", value]);
        options.SourceGenerators.Should().Be(expected);
    }

    [Fact]
    public void Parse_InvalidGeneratorPolicy_Throws()
    {
        var act = () => ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o", "--source-generators", "bogus"]);
        act.Should().Throw<ArgumentException>().WithMessage("*skip|materialize|reference*");
    }

    [Fact]
    public void Parse_NoExclude_LeavesExcludePathsNull()
    {
        var options = ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o"]);
        options.ExcludePaths.Should().BeNull("no --exclude means no exclusions are configured");
    }

    [Fact]
    public void Parse_Exclude_IsRepeatable_AndRootsEachPath()
    {
        var options = ArgParser.Parse([
            "-t", "N.T", "-s", "a.sln", "-o", "o",
            "--exclude", "vendor",
            "-x", Path.Combine("gen", "out"),
        ]);

        options.ExcludePaths.Should().NotBeNull();
        options.ExcludePaths!.Should().HaveCount(2, "--exclude and its -x alias each contribute a path");
        options.ExcludePaths.Should().OnlyContain(p => Path.IsPathRooted(p), "exclude paths are normalized to absolute");
        options.ExcludePaths.Should().Contain(p => p.EndsWith("vendor"));
    }

    [Fact]
    public void Parse_Exclude_MissingValue_Throws()
    {
        var act = () => ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o", "--exclude"]);
        act.Should().Throw<ArgumentException>().WithMessage("*Missing value for --exclude*");
    }

    [Fact]
    public void Parse_ExcludeFrom_ReadsLines_SkipsBlanksAndComments_StripsQuotes_AndMergesWithExclude()
    {
        var dir = FixturePaths.CreateTempOutputDir(nameof(Parse_ExcludeFrom_ReadsLines_SkipsBlanksAndComments_StripsQuotes_AndMergesWithExclude));
        var listFile = Path.Combine(dir, "excludes.txt");
        var absVendor = Path.Combine(dir, "Vendor");
        File.WriteAllLines(listFile, [
            "# a comment line is ignored",
            "",
            "   ",
            absVendor,                         // absolute path, used as-is
            $"\"{Path.Combine(dir, "Quoted")}\"", // surrounding quotes stripped
        ]);

        var options = ArgParser.Parse([
            "-t", "N.T", "-s", "a.sln", "-o", "o",
            "-x", Path.Combine(dir, "Direct"),  // a direct --exclude is merged with the file's entries
            "--exclude-from", listFile,
        ]);

        options.ExcludePaths.Should().NotBeNull();
        options.ExcludePaths!.Should().HaveCount(3, "1 direct --exclude + 2 real lines (blanks/comments skipped)");
        options.ExcludePaths.Should().OnlyContain(p => Path.IsPathRooted(p) && !p.Contains('"'));
        options.ExcludePaths.Should().Contain(absVendor);
        options.ExcludePaths.Should().Contain(p => p.EndsWith("Quoted"), "surrounding quotes are stripped per line");
        options.ExcludePaths.Should().Contain(p => p.EndsWith("Direct"), "direct --exclude entries are merged in");
    }

    [Fact]
    public void Parse_ExcludeFrom_ResolvesRelativeLines_AgainstFileDirectory()
    {
        var dir = FixturePaths.CreateTempOutputDir(nameof(Parse_ExcludeFrom_ResolvesRelativeLines_AgainstFileDirectory));
        var listFile = Path.Combine(dir, "excludes.txt");
        File.WriteAllLines(listFile, [Path.Combine("sub", "Generated")]);  // relative entry

        var options = ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o", "--exclude-from", listFile]);

        var expected = Path.GetFullPath(Path.Combine(dir, "sub", "Generated"));
        options.ExcludePaths.Should().ContainSingle().Which.Should().Be(expected,
            "relative lines resolve against the exclude file's own directory, not the CWD");
    }

    [Fact]
    public void Parse_ExcludeFrom_MissingFile_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), "chisel-no-such-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        var act = () => ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o", "--exclude-from", missing]);
        act.Should().Throw<ArgumentException>().WithMessage("*--exclude-from file not found*");
    }

    [Fact]
    public void Parse_ExcludeFrom_Dash_ReadsExclusionsFromStdin()
    {
        // The idiomatic PowerShell pipe: `$paths | chisel --exclude-from -`. "-" reads the list from
        // stdin; blanks/comments are skipped and relative entries resolve against the working dir.
        var absDir = Path.Combine(Path.GetTempPath(), "chisel-stdin-" + Guid.NewGuid().ToString("N")[..8], "Vendor");
        var savedIn = Console.In;
        try
        {
            Console.SetIn(new StringReader(
                "# piped in" + Environment.NewLine +
                "" + Environment.NewLine +
                absDir + Environment.NewLine));

            var options = ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o", "--exclude-from", "-"]);

            options.ExcludePaths.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(absDir),
                "the '-' source reads exclusion paths from stdin, skipping blanks and comments");
        }
        finally
        {
            Console.SetIn(savedIn);
        }
    }

    [Theory]
    [InlineData("--type")]
    [InlineData("--solution")]
    [InlineData("--output")]
    public void Parse_MissingValue_Throws(string flag)
    {
        var act = () => ArgParser.Parse([flag]);
        act.Should().Throw<ArgumentException>().WithMessage($"*Missing value for {flag}*");
    }

    [Fact]
    public void Parse_UnknownArgument_Throws()
    {
        var act = () => ArgParser.Parse(["-t", "N.T", "-s", "a.sln", "-o", "o", "--frobnicate"]);
        act.Should().Throw<ArgumentException>().WithMessage("*Unknown argument: --frobnicate*");
    }

    [Theory]
    [InlineData(new[] { "-s", "a.sln", "-o", "o" }, "--type")]
    [InlineData(new[] { "-t", "N.T", "-o", "o" }, "--solution")]
    [InlineData(new[] { "-t", "N.T", "-s", "a.sln" }, "--output")]
    public void Parse_MissingRequired_Throws(string[] args, string missing)
    {
        var act = () => ArgParser.Parse(args);
        act.Should().Throw<ArgumentException>().WithMessage($"*{missing} is required*");
    }

    [Fact]
    public async Task CliEntry_EmptyArgs_PrintsUsage_AndReturns1()
    {
        (await CliEntry.RunAsync([])).Should().Be(1);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task CliEntry_Help_Returns0(string flag)
    {
        (await CliEntry.RunAsync([flag])).Should().Be(0);
    }

    [Fact]
    public async Task CliEntry_BadArguments_Returns2()
    {
        (await CliEntry.RunAsync(["--type"])).Should().Be(2);
        (await CliEntry.RunAsync(["--nonsense"])).Should().Be(2);
    }
}
