using FluentAssertions;
using Bennewitz.Ninja.Chisel.Collection;

namespace Bennewitz.Ninja.Chisel.Tests;

public sealed class ExternalReferencePathTests
{
    [Theory]
    // Default global packages folder.
    [InlineData(@"C:\Users\me\.nuget\packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.dll", "newtonsoft.json", "13.0.3")]
    // Custom global packages folder (NUGET_PACKAGES / globalPackagesFolder) — the real-world case
    // that was producing 0 packages.
    [InlineData(@"C:\c\allos\.nuget_cache\autofac\9.0.0\lib\net10.0\Autofac.dll", "autofac", "9.0.0")]
    [InlineData(@"C:\c\allos\.nuget_cache\awssdk.s3\4.0.13.1\lib\net8.0\AWSSDK.S3.dll", "awssdk.s3", "4.0.13.1")]
    // Package shipping ref assemblies.
    [InlineData(@"/home/u/.nuget/packages/somepkg/2.1.0-beta/ref/net8.0/SomePkg.dll", "somepkg", "2.1.0-beta")]
    public void TryParsePackageFromPath_RecognizesPackages(string path, string id, string version)
    {
        var result = ExternalReferenceCollector.TryParsePackageFromPath(path);
        result.Should().NotBeNull();
        result!.Value.Id.Should().Be(id);
        result.Value.Version.Should().Be(version);
    }

    [Theory]
    // .NET targeting packs are SDK-supplied, not NuGet packages.
    [InlineData(@"C:\Program Files\dotnet\packs\Microsoft.AspNetCore.App.Ref\10.0.8\ref\net10.0\Microsoft.AspNetCore.Http.dll")]
    [InlineData(@"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.8\ref\net10.0\System.Runtime.dll")]
    // Downlevel framework ref packs resolve from the NuGet cache (e.g. building net8.0 with the .NET
    // 10 SDK) — same package layout as a real package, but must NOT become a PackageReference (NU1213).
    [InlineData(@"/home/u/.nuget/packages/microsoft.netcore.app.ref/8.0.27/ref/net8.0/System.Runtime.dll")]
    [InlineData(@"C:\Users\me\.nuget\packages\microsoft.aspnetcore.app.ref\8.0.11\ref\net8.0\Microsoft.AspNetCore.Http.dll")]
    [InlineData(@"/home/u/.nuget/packages/microsoft.netcore.app.host.linux-x64/8.0.27/runtimes/linux-x64/native/apphost")]
    // A loose project build output / non-package reference.
    [InlineData(@"C:\src\MyLib\bin\Debug\net10.0\MyLib.dll")]
    [InlineData(null)]
    [InlineData("")]
    public void TryParsePackageFromPath_RejectsNonPackages(string? path)
    {
        ExternalReferenceCollector.TryParsePackageFromPath(path).Should().BeNull();
    }
}
