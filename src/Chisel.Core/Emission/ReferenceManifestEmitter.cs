using System.Text.Json;
using Bennewitz.Ninja.Chisel.Collection;

namespace Bennewitz.Ninja.Chisel.Emission;

public static class ReferenceManifestEmitter
{
    public static async Task WriteAsync(string path, IEnumerable<ExternalReference> references, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var list = references.ToList();
        var packages = list.Where(r => r.PackageId is not null).Select(r => new
        {
            id = r.PackageId,
            version = r.PackageVersion,
            assemblyName = r.AssemblyName,
            assemblyVersion = r.AssemblyVersion,
        });
        var frameworkAssemblies = list.Where(r => r.PackageId is null).Select(r => new
        {
            name = r.AssemblyName,
            version = r.AssemblyVersion,
            path = r.ReferenceFilePath,
        });

        var payload = new
        {
            packages,
            frameworkAssemblies,
        };

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, payload, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
    }
}
