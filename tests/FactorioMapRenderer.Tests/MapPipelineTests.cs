using System.Text.Json;
using FactorioMapRenderer.Game;
using FactorioMapRenderer.Rendering;
using FactorioMapRenderer.Tests.TestSupport;
using FactorioMapRenderer.World;

namespace FactorioMapRenderer.Tests;

public sealed class MapExportTests
{
    [Fact]
    public void Run_length_coded_chunks_decode_to_1024_tiles()
    {
        var codes = new Dictionary<int, int> { [0] = 0, [7] = 1 };
        var tiles = MapExport.DecodeChunk("7*3,0,7*1020", codes);
        Assert.Equal(1024, tiles.Length);
        Assert.Equal([1, 1, 1, 0, 1], tiles[..5].Select(t => (int)t));
        Assert.Equal(1, tiles[^1]);
    }

    [Fact]
    public void Reads_meta_and_surface()
    {
        string dir = SyntheticExport.Write(TestFiles.TempDirectory());
        try
        {
            var export = MapExport.Read(dir, SyntheticExport.Header, "test.zip", null);
            var surface = Assert.Single(export.Surfaces);
            Assert.Equal("nauvis", surface.Planet);
            Assert.Equal(4, surface.Chunks.Count);
            Assert.Equal(24, surface.Resources.Count);
            Assert.Single(surface.Wires);
            Assert.Equal("Home", Assert.Single(surface.Tags).Text);
            Assert.Equal(12, surface.Counted["small-biter"]);
            Assert.Equal(1200, surface.ItemsProduced["iron-plate"]);
            Assert.Equal("Engineer", Assert.Single(export.Players).Name);
            Assert.Equal(2, export.PlayerForce!.RocketsLaunched);
            // the export mod itself is not one of the save's mods
            Assert.DoesNotContain(FactorioRunner.ExportMod, export.Mods.Keys);
            // water in the first rows of the top left chunk
            Assert.Equal("water", surface.Tiles[surface.Chunks[(-1, -1)][0]].Name);
            Assert.Equal("grass-1", surface.Tiles[surface.Chunks[(-1, -1)][1023]].Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Clusters_points_that_lie_close_together()
    {
        var points = new List<(float X, float Y)> { (0, 0), (1, 0), (2, 1), (50, 50), (51, 50), (200, 0) };
        var clusters = SurfaceMap.Cluster(points, p => p, 3);
        Assert.Equal([3, 2, 1], clusters.Select(c => c.Count).OrderDescending());
    }

    [Fact]
    public void Ticks_and_amounts_read_well()
    {
        Assert.Equal("1 h 00 min", SurfaceMap.Duration(216000));
        Assert.Equal("2 d 1 h", SurfaceMap.Duration(60L * 3600 * 49));
        Assert.Equal("1.5M", SurfaceMap.Amount(1_500_000));
        Assert.Equal("950", SurfaceMap.Amount(950));
    }
}

public sealed class MapPipelineTests
{
    [Fact]
    public void Export_becomes_layers_markers_tiles_and_a_page()
    {
        string root = TestFiles.TempDirectory();
        try
        {
            string exportDir = SyntheticExport.Write(Path.Combine(root, "export"));
            string output = Path.Combine(root, "map");
            var export = MapExport.Read(exportDir, SyntheticExport.Header, "test.zip", DateTime.UtcNow);
            var game = GameData.Empty;
            var surface = SurfaceMap.Build(export.Surfaces[0], export, game)!;

            Assert.Equal("Nauvis", surface.Title);
            Assert.Equal((-32, -32, 32, 32), surface.Bounds);
            Assert.Single(surface.Raster[Layers.Buildings]);
            Assert.Equal(8, surface.Raster[Layers.Belts].Count);
            Assert.Equal(3, surface.Raster[Layers.Power].Count); // two poles and their wire
            Assert.Single(surface.Raster[Layers.Enemies]);
            Assert.Single(surface.Markers[Layers.Patches].Markers);
            Assert.Equal("36k", surface.Markers[Layers.Patches].Markers[0].Details[1][1]);
            Assert.Single(surface.Markers[Layers.Nests].Markers);
            Assert.Equal("Iron [item=iron-plate] pickup", Assert.Single(surface.Markers[Layers.Stations].Markers).Name);
            Assert.Single(surface.Markers[Layers.Players].Markers);
            var machine = Assert.Single(surface.Inspect);
            Assert.Contains(machine.Details, row => row[0] == "Recipe" && row[1] == "Iron gear wheel");
            Assert.Equal(["Water", "Iron plate", "Iron gear wheel"], surface.Production.Select(p => p.Name));

            Directory.CreateDirectory(output);
            var tiles = TileRenderer.Render(surface, Path.Combine(output, "tiles", "1"), 7, game, _ => { });
            HtmlExporter.Export(export, [surface], new Dictionary<int, Dictionary<string, TileSet>> { [1] = tiles }, output, 7, game);

            // the ground is drawn at one pixel per tile, the factory at the highest zoom
            Assert.Equal(5, tiles["terrain"].MaxNativeZoom);
            Assert.Equal(7, tiles["buildings"].MaxNativeZoom);
            foreach (var (layer, set) in tiles)
                foreach (string tile in set.Tiles)
                    Assert.True(File.Exists(Path.Combine(output, "tiles", "1", layer, tile + ".png")), $"{layer}/{tile}");
            Assert.True(File.Exists(Path.Combine(output, "index.html")));
            Assert.True(File.Exists(Path.Combine(output, "viewer.js")));
            Assert.True(File.Exists(Path.Combine(output, "leaflet", "leaflet.js")));
            Assert.False(Directory.Exists(Path.Combine(output, "exporter")), "the export mod is not part of the page");

            string data = File.ReadAllText(Path.Combine(output, "map-data.js"));
            using var doc = JsonDocument.Parse(data[data.IndexOf('{')..(data.LastIndexOf('}') + 1)]);
            var s = Assert.Single(doc.RootElement.GetProperty("surfaces").EnumerateArray());
            Assert.Equal("nauvis", s.GetProperty("name").GetString());
            Assert.Contains(doc.RootElement.GetProperty("summary").EnumerateArray(), row => row[0].GetString() == "Rockets launched");
            Assert.Matches("^[0-9a-f]{12}$", doc.RootElement.GetProperty("build").GetString());
            Assert.StartsWith("(window.MAP_SURFACES = window.MAP_SURFACES || {})[1] = ", File.ReadAllText(Path.Combine(output, "surfaces", "1.js")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Close_up_the_game_graphics_are_layers_of_their_own()
    {
        string root = TestFiles.TempDirectory();
        try
        {
            // game data with one sprite (the assembling machine, 3 × 3 tiles) and one ground texture (grass, 2 variants)
            string data = Path.Combine(root, "gamedata");
            Directory.CreateDirectory(Path.Combine(data, "sprites", "assembling-machine-1"));
            Directory.CreateDirectory(Path.Combine(data, "tiles"));
            using (var machine = new SkiaSharp.SKBitmap(192, 192))
            {
                machine.Erase(SkiaSharp.SKColors.OrangeRed);
                using var file = File.Create(Path.Combine(data, "sprites", "assembling-machine-1", "0.png"));
                machine.Encode(file, SkiaSharp.SKEncodedImageFormat.Png, 100);
            }
            using (var grass = new SkiaSharp.SKBitmap(128, 64))
            {
                grass.Erase(SkiaSharp.SKColors.ForestGreen);
                using var file = File.Create(Path.Combine(data, "tiles", "grass-1.jpg"));
                grass.Encode(file, SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
            }
            File.WriteAllText(Path.Combine(data, "gamedata.json"), """
                {"format":2,"gameVersion":"2.1.23","chart":{},"names":{},"icons":[],"entityIcons":{},
                 "sprites":{"assembling-machine-1":{"0":[-1.5,-1.5,3,3]}},"tiles":{"grass-1":[0,2,1,1]},"stages":{},
                 "space":{"locations":[],"connections":[]}}
                """);
            var game = GameData.Load(data, _ => { })!;
            Assert.True(game.HasGraphics);

            var export = MapExport.Read(SyntheticExport.Write(Path.Combine(root, "export")), SyntheticExport.Header, "test.zip", null);
            var surface = SurfaceMap.Build(export.Surfaces[0], export, game)!;
            var sprite = Assert.Single(surface.Raster[Layers.Graphics], s => s.Kind == ShapeKind.Sprite);
            Assert.Equal((-6.5f, -6.5f, -3.5f, -3.5f), (sprite.X1, sprite.Y1, sprite.X2, sprite.Y2));
            // the belts have no sprite here: their footprint stands in
            Assert.Contains(surface.Raster[Layers.Graphics], s => s.Kind == ShapeKind.Rect);

            // the browser draws them from the game's files: the page gets the files and what lies where
            string output = Path.Combine(root, "map");
            var tiles = TileRenderer.Render(surface, Path.Combine(output, "tiles", "1"), 9, game, _ => { });
            Assert.DoesNotContain("graphics", tiles.Keys);
            HtmlExporter.Export(export, [surface], new Dictionary<int, Dictionary<string, TileSet>> { [1] = tiles }, output, 9, game);
            Assert.True(File.Exists(Path.Combine(output, "sprites", "assembling-machine-1", "0.png")));
            Assert.True(File.Exists(Path.Combine(output, "ground", "grass-1.jpg")));
            // the four chunks around the origin lie in four regions
            string region = File.ReadAllText(Path.Combine(output, "surfaces", "1", "-1_-1.js"));
            Assert.StartsWith("FMR_REGION(1,\"-1_-1\",", region);
            Assert.Contains("-6.5,-6.5", region);
            string page = File.ReadAllText(Path.Combine(output, "surfaces", "1.js"));
            Assert.Contains("\"closeUp\"", page);
            Assert.Contains("sprites/assembling-machine-1/0.png", page);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_terrain_has_the_colour_of_each_tile()
    {
        string root = TestFiles.TempDirectory();
        try
        {
            var export = MapExport.Read(SyntheticExport.Write(Path.Combine(root, "export")), SyntheticExport.Header, "test.zip", null);
            var surface = SurfaceMap.Build(export.Surfaces[0], export, GameData.Empty)!;
            TileRenderer.Render(surface, Path.Combine(root, "tiles"), 9, GameData.Empty, _ => { });

            // zoom 5: one pixel per game tile; the tile -1/-1 covers game tiles -256 to -1
            using var image = SkiaSharp.SKBitmap.Decode(Path.Combine(root, "tiles", "terrain", "5", "-1", "-1.png"));
            Assert.Equal(new SkiaSharp.SKColor(51, 83, 95), image.GetPixel(256 - 32, 256 - 32));   // water at the chunk's top
            Assert.Equal(new SkiaSharp.SKColor(55, 83, 35), image.GetPixel(255, 255));             // grass
            Assert.Equal(SkiaSharp.SKColors.Transparent.Alpha, image.GetPixel(0, 0).Alpha);         // not charted
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
