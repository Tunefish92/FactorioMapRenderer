using FactorioMapRenderer.Game;
using FactorioMapRenderer.Save;
using FactorioMapRenderer.Tests.TestSupport;
using FactorioMapRenderer.World;

namespace FactorioMapRenderer.Tests;

/// <summary>
/// The export mod in a real game: needs a Factorio for Linux (FMR_TEST_GAME, or the one in basegame/) and a
/// real save (FMR_TEST_SAVES, or the ones in savegame/). Skipped without them.
/// </summary>
public sealed class RealGameTests
{
    [Fact]
    public void The_game_exports_every_surface_of_a_save()
    {
        var saves = TestFiles.RealSaves();
        if (TestFiles.Game() is not { } gameDirectory || FactorioInstallation.TryOpen(gameDirectory) is not { Binary: not null } game || saves.Count == 0)
        {
            Assert.Skip("Needs a Factorio for Linux and a save: set FMR_TEST_GAME and FMR_TEST_SAVES.");
            return;
        }
        string work = TestFiles.TempDirectory();
        try
        {
            var header = SaveHeader.Read(saves[0]);
            if (header.GameVersion > game.Version)
                Assert.Skip($"The save ({header.GameVersionText}) is newer than the game ({game.VersionText}).");
            string binary = game.PrepareBinary(Path.Combine(work, "cache"));
            string exported = FactorioRunner.Export(game, binary, saves[0], header, Path.Combine(work, "run"), null, _ => { });
            var export = MapExport.Read(exported, header, Path.GetFileName(saves[0]), null);

            Assert.NotEmpty(export.Surfaces);
            var main = export.Surfaces.OrderByDescending(s => s.Chunks.Count).First();
            Assert.NotEmpty(main.Chunks);
            Assert.NotEmpty(main.Tiles);
            Assert.All(main.Chunks.Values, tiles => Assert.Equal(1024, tiles.Length));
            Assert.NotEmpty(export.Forces);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
