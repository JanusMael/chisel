using System.Text.Json;
using Bennewitz.Ninja.Chisel.Collection;

namespace Bennewitz.Ninja.Chisel.Emission;

public static class FileListEmitter
{
    public static async Task WriteAsync(string path, IEnumerable<CollectedFile> files, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new
        {
            files = files
                .OrderBy(f => f.ProjectName, StringComparer.Ordinal)
                .ThenBy(f => f.AbsolutePath, StringComparer.Ordinal)
                .Select(f => new
                {
                    path = f.AbsolutePath,
                    project = f.ProjectName,
                    targetFramework = f.TargetFramework,
                    isGenerated = f.IsGenerated,
                    containsSymbols = f.ContainingSymbols.OrderBy(s => s, StringComparer.Ordinal),
                }),
        };

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, payload, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
    }
}
