using System.IO.Compression;
using FactorioMapRenderer.Save;
using FactorioMapRenderer.Tests.TestSupport;

namespace FactorioMapRenderer.Tests;

public sealed class SaveHeaderTests
{
    [Fact]
    public void Reads_version_level_and_mods()
    {
        var header = SaveHeader.Parse(SyntheticExport.LevelHeader(2, 0, 32, "freeplay",
            ("base", 2, 0, 32), ("elevated-rails", 2, 0, 32), ("quality", 2, 0, 32), ("space-age", 2, 0, 32)), "world");

        Assert.Equal("2.0.32", header.GameVersionText);
        Assert.Equal("freeplay", header.Scenario);
        Assert.Equal(["base", "elevated-rails", "quality", "space-age"], header.Mods.Select(m => m.Name));
        Assert.Equal("2.0.32", header.Mods[3].Version);
    }

    [Fact]
    public void Reads_a_zipped_save_with_compressed_level_data()
    {
        string file = Path.Combine(Path.GetTempPath(), $"fmr-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                using var stream = zip.CreateEntry("my-base/level.dat0").Open();
                using var deflate = new ZLibStream(stream, CompressionLevel.Optimal);
                deflate.Write(SyntheticExport.LevelHeader(2, 1, 23, "freeplay", ("base", 2, 1, 23), ("my-mod", 1, 4, 0)));
            }
            var header = SaveHeader.Read(file);
            Assert.Equal("my-base", header.Name);
            Assert.Equal(new Version(2, 1, 23, 0), header.GameVersion);
            Assert.Contains(header.Mods, m => m is { Name: "my-mod", Version: "1.4.0" });
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_zip_without_level_data_is_not_a_save()
    {
        string file = Path.Combine(Path.GetTempPath(), $"fmr-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
                zip.CreateEntry("readme.txt");
            Assert.Throws<SaveFormatException>(() => SaveHeader.Read(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_file_that_is_no_zip_is_not_a_save()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, [1, 2, 3, 4, 5]);
            Assert.Throws<SaveFormatException>(() => SaveHeader.Read(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Truncated_level_data_throws_format_error()
    {
        Assert.Throws<SaveFormatException>(() => SaveHeader.Parse([2, 0, 0, 0, 32, 0, 0, 0, 0, 8, (byte)'f'], "x"));
    }

    /// <summary>Real saves, given in FMR_TEST_SAVES (separated by ";"), or the one in savegame/.</summary>
    [Fact]
    public void Reads_real_saves()
    {
        var saves = TestFiles.RealSaves();
        if (saves.Count == 0)
            Assert.Skip("No real save: set FMR_TEST_SAVES or put one into savegame/.");
        foreach (string save in saves)
        {
            var header = SaveHeader.Read(save);
            Assert.True(header.GameVersion.Major >= 1, save);
            Assert.Contains(header.Mods, m => m.Name == "base");
        }
    }
}
