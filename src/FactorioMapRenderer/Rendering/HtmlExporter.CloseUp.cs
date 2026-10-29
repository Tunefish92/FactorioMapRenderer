using System.Globalization;
using System.Text;
using System.Text.Json;
using FactorioMapRenderer.Game;
using FactorioMapRenderer.World;

namespace FactorioMapRenderer.Rendering;

/// <summary>
/// The data from which the browser draws the game's own graphics when zoomed in: the ground (which tile lies where)
/// and every sprite with its place, in regions that the page loads as they come into view. The sprites and ground
/// textures themselves are the game data's files at the game's full resolution, copied next to the map, so the
/// browser draws them as sharp as the game does at any zoom.
/// </summary>
public static partial class HtmlExporter
{
    /// <summary>Edge length of a region, in game tiles.</summary>
    public const int RegionSize = 256;

    /// <summary>A region's ground: one byte per tile, the index into the surface's palette; this one is uncharted.</summary>
    private const byte Uncharted = 255;

    /// <summary>The sprite and texture files a map uses, copied once each into sprites/ and ground/.</summary>
    private sealed class CloseUpFiles
    {
        public GameData Game { get; }
        private readonly string _output;
        private readonly HashSet<string> _copied = new(StringComparer.Ordinal);

        public CloseUpFiles(GameData game, string output)
        {
            (Game, _output) = (game, output);
            foreach (string folder in new[] { "sprites", "ground" })
                if (Directory.Exists(Path.Combine(output, folder)))
                    Directory.Delete(Path.Combine(output, folder), recursive: true);
        }

        public string? Sprite(string key) => Copy(Game.SpriteFile(key), $"sprites/{key}.png");

        public string? Ground(string tile) => Copy(Game.TileFile(tile), $"ground/{tile}.jpg");

        private string? Copy(string source, string url)
        {
            if (_copied.Contains(url))
                return url;
            if (!File.Exists(source))
                return null;
            string target = Path.Combine(_output, url);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            _copied.Add(url);
            return url;
        }
    }

    private static void WriteCloseUp(Utf8JsonWriter w, SurfaceMap s, IconTable icons, CloseUpFiles files, string regionDirectory)
    {
        var game = files.Game;
        var shapes = s.Raster[Layers.Graphics];
        int Region(double v) => (int)Math.Floor(v / RegionSize);

        // what each region holds: its entities in drawing order, its charted ground
        var regions = new SortedDictionary<(int X, int Y), List<int>>();
        for (int i = 0; i < shapes.Count; i++)
        {
            var shape = shapes[i];
            var key = (Region((shape.X1 + shape.X2) / 2), Region((shape.Y1 + shape.Y2) / 2));
            if (!regions.TryGetValue(key, out var list))
                regions[key] = list = [];
            list.Add(i);
        }
        foreach (var (cx, cy) in s.Data.Chunks.Keys)
            regions.TryAdd((Region(cx * 32), Region(cy * 32)), []);

        // the sprites, each once: url and where it lies around its entity (width and height in tiles)
        var sprites = new Dictionary<string, int>(StringComparer.Ordinal);
        var spriteList = new List<(string Url, float W, float H)>();

        Directory.CreateDirectory(regionDirectory);
        foreach (var ((rx, ry), items) in regions)
        {
            using var stream = File.Create(Path.Combine(regionDirectory, $"{rx}_{ry}.js"));
            stream.Write(Encoding.ASCII.GetBytes($"FMR_REGION({s.Data.Index},\"{rx}_{ry}\","));
            using (var r = new Utf8JsonWriter(stream, WriterOptions))
            {
                r.WriteStartObject();
                r.WriteString("g", Ground(s.Data, rx, ry));
                // sprites: order, sprite, left, top; footprints: order, icon, left, top, right, bottom
                r.WriteStartArray("s");
                foreach (int i in items.Where(i => shapes[i].Kind == ShapeKind.Sprite))
                {
                    var shape = shapes[i];
                    if (!sprites.TryGetValue(shape.Icon!, out int index))
                    {
                        if (files.Sprite(shape.Icon!) is not { } url)
                            continue;
                        sprites[shape.Icon!] = index = spriteList.Count;
                        spriteList.Add((url, shape.X2 - shape.X1, shape.Y2 - shape.Y1));
                    }
                    r.WriteNumberValue(i);
                    r.WriteNumberValue(index);
                    r.WriteNumberValue(Math.Round(shape.X1, 3));
                    r.WriteNumberValue(Math.Round(shape.Y1, 3));
                }
                r.WriteEndArray();
                r.WriteStartArray("f");
                foreach (int i in items.Where(i => shapes[i].Kind == ShapeKind.Rect))
                {
                    var shape = shapes[i];
                    r.WriteNumberValue(i);
                    r.WriteNumberValue(icons[shape.Icon]);
                    foreach (float v in new[] { shape.X1, shape.Y1, shape.X2, shape.Y2 })
                        r.WriteNumberValue(Math.Round(v, 2));
                }
                r.WriteEndArray();
                // wires: order, x1, y1, x2, y2
                r.WriteStartArray("w");
                foreach (int i in items.Where(i => shapes[i].Kind == ShapeKind.Line))
                {
                    var shape = shapes[i];
                    r.WriteNumberValue(i);
                    foreach (float v in new[] { shape.X1, shape.Y1, shape.X2, shape.Y2 })
                        r.WriteNumberValue(Math.Round(v, 2));
                }
                r.WriteEndArray();
                r.WriteEndObject();
            }
            stream.Write(");\n"u8);
        }

        w.WriteStartObject("closeUp");
        w.WriteNumber("minZoom", TileRenderer.CloseUpZoom);
        w.WriteNumber("regionSize", RegionSize);
        w.WriteString("regions", string.Join(' ', regions.Keys.Select(k => $"{k.X}_{k.Y}")));
        // per tile type: map colour, texture, whether it repeats over the ground, its variants or size in tiles
        w.WriteStartArray("palette");
        foreach (var tile in s.Data.Tiles)
        {
            w.WriteStartArray();
            w.WriteStringValue(SurfaceMap.Hex(tile.Color));
            var texture = game.Tile(tile.Name);
            w.WriteStringValue(texture is null ? "" : files.Ground(tile.Name) ?? "");
            w.WriteNumberValue(texture is { Material: true } ? 1 : 0);
            w.WriteNumberValue(texture?.Count ?? 1);
            w.WriteNumberValue(texture?.Width ?? 1);
            w.WriteNumberValue(texture?.Height ?? 1);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteStartArray("sprites");
        foreach (var (url, width, height) in spriteList)
        {
            w.WriteStartArray();
            w.WriteStringValue(url);
            w.WriteNumberValue(Math.Round(width, 3));
            w.WriteNumberValue(Math.Round(height, 3));
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>The region's tiles row by row, one byte each (an index into the palette), as base64.</summary>
    private static string Ground(SurfaceData surface, int rx, int ry)
    {
        var bytes = new byte[RegionSize * RegionSize];
        Array.Fill(bytes, Uncharted);
        int chunks = RegionSize / 32;
        for (int cy = 0; cy < chunks; cy++)
        for (int cx = 0; cx < chunks; cx++)
        {
            if (!surface.Chunks.TryGetValue((rx * chunks + cx, ry * chunks + cy), out var tiles))
                continue;
            for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                bytes[(cy * 32 + y) * RegionSize + cx * 32 + x] = (byte)Math.Min((int)tiles[y * 32 + x], Uncharted - 1);
        }
        return Convert.ToBase64String(bytes);
    }
}
