namespace Bennewitz.Ninja.Chisel.Tests;

internal static class FixturePaths
{
    public static string RepoRoot { get; } = LocateRepoRoot();
    public static string FixturesRoot => Path.Combine(RepoRoot, "tests", "Fixtures");

    public static string Solution(string fixtureName, string slnFileName)
        => Path.Combine(FixturesRoot, fixtureName, slnFileName);

    public static string CreateTempOutputDir(string testName)
    {
        var path = Path.Combine(Path.GetTempPath(), "rps-tests", testName + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string LocateRepoRoot()
    {
        // Walk up from this assembly's location until we find Chisel.slnx.
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Chisel.slnx"))
                || File.Exists(Path.Combine(dir, "Chisel.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repo root (no Chisel.slnx found above " + AppContext.BaseDirectory + ").");
    }
}
