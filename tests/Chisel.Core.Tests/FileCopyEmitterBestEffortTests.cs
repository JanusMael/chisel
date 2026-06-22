using FluentAssertions;
using Bennewitz.Ninja.Chisel.Collection;
using Bennewitz.Ninja.Chisel.Diagnostics;
using Bennewitz.Ninja.Chisel.Emission;

namespace Bennewitz.Ninja.Chisel.Tests;

/// <summary>
/// Proves the copy stage is best-effort: a dangling file (referenced but missing on disk) or a
/// generated file with no captured text is reported as a non-fatal diagnostic and skipped, while
/// the healthy files are still copied and mapped. Nothing throws.
/// </summary>
public sealed class FileCopyEmitterBestEffortTests
{
    [Fact]
    public void Copy_SkipsDanglingFiles_StillCopiesGoodOnes_AndReports()
    {
        var work = Directory.CreateTempSubdirectory("rps-copy-test");
        try
        {
            // A real, present source file.
            var projDir = Path.Combine(work.FullName, "proj");
            Directory.CreateDirectory(projDir);
            var projPath = Path.Combine(projDir, "Proj.csproj");
            var goodPath = Path.Combine(projDir, "Good.cs");
            File.WriteAllText(goodPath, "namespace P; public class Good { }");

            var missingPath = Path.Combine(projDir, "does-not-exist", "Missing.cs");

            var files = new List<CollectedFile>
            {
                new(goodPath, "Proj", projPath, "net10.0", [], IsGenerated: false),
                // Dangling: referenced by a symbol but not present on disk.
                new(missingPath, "Proj", projPath, "net10.0", [], IsGenerated: false),
                // Generated but with no captured text to materialize.
                new(Path.Combine("synthetic", "Gen.g.cs"), "Proj", projPath, "net10.0", [], IsGenerated: true, GeneratedText: null),
            };

            var outRoot = Path.Combine(work.FullName, "out");
            var sink = new DiagnosticSink();

            var act = () => FileCopyEmitter.Copy(outRoot, files, sink);

            var mapping = act.Should().NotThrow("a dangling file must not abort the copy").Subject;

            // Only the good file is mapped and physically present.
            mapping.Should().ContainKey(goodPath);
            mapping.Should().NotContainKey(missingPath);
            File.Exists(mapping[goodPath]).Should().BeTrue();

            // Both problems were reported (non-fatally).
            sink.Items.Should().HaveCount(2);
            sink.Items.Should().Contain(d => d.Item == missingPath && d.Message.Contains("not present on disk"));
            sink.Items.Should().Contain(d => d.Message.Contains("no captured text"));
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }
}
