namespace FactorioMapRenderer.Tests.TestSupport;

internal static class TestFiles
{
    /// <summary>The repository's root: the folder with FactorioMapRenderer.slnx.</summary>
    public static string? Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FactorioMapRenderer.slnx")))
                return dir.FullName;
        return null;
    }

    /// <summary>Saves from FMR_TEST_SAVES ("a.zip;b.zip"), or else the ones in savegame/.</summary>
    public static List<string> RealSaves()
    {
        if (Environment.GetEnvironmentVariable("FMR_TEST_SAVES") is { Length: > 0 } list)
            return list.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        string? folder = Root() is { } root ? Path.Combine(root, "savegame") : null;
        return folder is not null && Directory.Exists(folder) ? Directory.GetFiles(folder, "*.zip").ToList() : [];
    }

    /// <summary>A Factorio for Linux from FMR_TEST_GAME, or else the one in basegame/.</summary>
    public static string? Game()
    {
        if (Environment.GetEnvironmentVariable("FMR_TEST_GAME") is { Length: > 0 } game)
            return game;
        string? folder = Root() is { } root ? Path.Combine(root, "basegame") : null;
        return folder is not null && Directory.Exists(folder) ? folder : null;
    }

    public static string TempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"fmr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
