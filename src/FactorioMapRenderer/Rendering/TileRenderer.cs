using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FactorioMapRenderer.Game;
using FactorioMapRenderer.World;
using SkiaSharp;

namespace FactorioMapRenderer.Rendering;

/// <summary>The tiles of one raster layer: which exist, and from which zoom levels.</summary>
public sealed record TileSet(int MinNativeZoom, int MaxNativeZoom, IReadOnlyList<string> Tiles);

/// <summary>
/// Cuts the raster layers of a surface into 256 px image tiles for Leaflet. Every surface shares one scale:
/// at zoom z a game tile is 2^z / 32 px wide, so zoom 9 shows 16 px per tile and zoom 5 one pixel per tile.
/// The highest zoom of a layer is drawn from the data; every lower one is made of the four tiles below it.
/// </summary>
/// <remarks>
/// The ground and the resources are one colour per game tile: at one pixel per tile, enlarged by the browser
/// without smoothing, they look the same at every zoom, for a sixteenth of the tiles at zoom 9 and above.
/// </remarks>
public static class TileRenderer
{
    public const int TileSize = 256;
    /// <summary>Pixels per game tile at zoom 0.</summary>
    public const double PixelsPerTileAtZoom0 = 1.0 / 32;

    /// <summary>Game tiles across one image tile at zoom <paramref name="zoom"/>.</summary>
    public static double TilesPerImage(int zoom) => TileSize / (PixelsPerTileAtZoom0 * Math.Pow(2, zoom));

    /// <summary>The zoom at which the whole surface fits into about one image tile.</summary>
    public static int MinZoom(SurfaceMap map, int maxZoom)
    {
        int extent = Math.Max(map.Bounds.MaxX - map.Bounds.MinX, map.Bounds.MaxY - map.Bounds.MinY);
        return Math.Clamp((int)Math.Floor(Math.Log2(TileSize / PixelsPerTileAtZoom0 / Math.Max(extent, 32))), -4, maxZoom);
    }

    /// <summary>The highest zoom a layer needs: more would only enlarge its blocks of colour.</summary>
    public static int NativeZoom(LayerDef layer, int maxZoom) => Math.Min(maxZoom,
        layer == Layers.Terrain || layer == Layers.Resources ? 5 : layer == Layers.Nature ? 7 : maxZoom);

    /// <summary>
    /// The game's own graphics are shown from this zoom on (4 px per tile); further out the map view reads better,
    /// as in the game, which switches from its world to its map the same way. The browser draws them itself
    /// (see <see cref="CloseUpExporter"/>), not from tiles: so they are as sharp as the game's files at every zoom.
    /// </summary>
    public const int CloseUpZoom = 7;

    public static Dictionary<string, TileSet> Render(SurfaceMap map, string directory, int maxZoom, GameData game, Action<string> log,
        Action<double>? progress = null)
    {
        var icons = new ConcurrentDictionary<string, SKImage?>(StringComparer.Ordinal);
        var result = new Dictionary<string, TileSet>(StringComparer.Ordinal);
        var raster = Layers.All.Where(l => l.Kind == LayerKind.Raster && l != Layers.Ground && l != Layers.Graphics).ToList();
        for (int li = 0; li < raster.Count; li++)
        {
            var layer = raster[li];
            int top = NativeZoom(layer, maxZoom), minZoom = Math.Min(MinZoom(map, maxZoom), top);
            double span = TilesPerImage(top);
            // which image tiles at the highest zoom have something of this layer
            var buckets = new Dictionary<(int X, int Y), List<int>>();
            var shapes = layer == Layers.Terrain ? [] : map.Raster[layer];
            if (layer == Layers.Terrain)
            {
                foreach (var (cx, cy) in map.Data.Chunks.Keys)
                    foreach (var key in Covered(cx * 32, cy * 32, cx * 32 + 32, cy * 32 + 32, span))
                        buckets.TryAdd(key, []);
            }
            else
            {
                for (int i = 0; i < shapes.Count; i++)
                {
                    var s = shapes[i];
                    float pad = s.Kind == ShapeKind.Line ? 1 : 0;
                    foreach (var key in Covered(Math.Min(s.X1, s.X2) - pad, Math.Min(s.Y1, s.Y2) - pad, Math.Max(s.X1, s.X2) + pad, Math.Max(s.Y1, s.Y2) + pad, span))
                    {
                        if (!buckets.TryGetValue(key, out var list))
                            buckets[key] = list = [];
                        list.Add(i);
                    }
                }
            }
            if (buckets.Count == 0)
                continue;

            string layerDirectory = Path.Combine(directory, layer.Id);
            var written = new ConcurrentBag<string>();
            Parallel.ForEach(buckets, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, bucket =>
            {
                using var bitmap = layer == Layers.Terrain
                    ? DrawTerrain(map.Data, bucket.Key.X, bucket.Key.Y, span)
                    : DrawShapes(shapes, bucket.Value, bucket.Key.X, bucket.Key.Y, span, game, icons);
                Save(bitmap, layerDirectory, top, bucket.Key.X, bucket.Key.Y);
                written.Add($"{top}/{bucket.Key.X}/{bucket.Key.Y}");
            });

            // every lower zoom from the four tiles below
            var level = buckets.Keys.ToHashSet();
            for (int z = top - 1; z >= minZoom; z--)
            {
                var parents = level.Select(k => (X: k.X >> 1, Y: k.Y >> 1)).ToHashSet();
                int childZoom = z + 1;
                Parallel.ForEach(parents, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, parent =>
                {
                    using var bitmap = Downsample(layerDirectory, childZoom, parent.X, parent.Y, level);
                    Save(bitmap, layerDirectory, z, parent.X, parent.Y);
                    written.Add($"{z}/{parent.X}/{parent.Y}");
                });
                level = parents;
            }
            result[layer.Id] = new TileSet(minZoom, top, [.. written.OrderBy(t => t, StringComparer.Ordinal)]);
            log($"    {map.Title} – {layer.Title}: {written.Count:N0} tiles");
            progress?.Invoke((li + 1.0) / raster.Count);
        }
        foreach (var image in icons.Values)
            image?.Dispose();
        return result;
    }

    /// <summary>Image tiles at the highest zoom that a rectangle (in game tiles) touches.</summary>
    private static IEnumerable<(int X, int Y)> Covered(double x1, double y1, double x2, double y2, double span)
    {
        int tx1 = (int)Math.Floor(x1 / span), ty1 = (int)Math.Floor(y1 / span);
        int tx2 = (int)Math.Floor((x2 - 1e-6) / span), ty2 = (int)Math.Floor((y2 - 1e-6) / span);
        for (int x = tx1; x <= tx2; x++)
            for (int y = ty1; y <= ty2; y++)
                yield return (x, y);
    }

    /// <summary>The ground, one block of colour per game tile; uncharted chunks stay transparent.</summary>
    private static SKBitmap DrawTerrain(SurfaceData surface, int tx, int ty, double span)
    {
        var pixels = new int[TileSize * TileSize];
        var colors = surface.Tiles.Select(t => (int)((uint)0xFF << 24 | (uint)t.Color.Blue << 16 | (uint)t.Color.Green << 8 | t.Color.Red)).ToArray();
        double perTile = TileSize / span;
        for (int py = 0; py < TileSize; py++)
        {
            int wy = (int)Math.Floor(ty * span + (py + 0.5) / perTile);
            int cy = wy >> 5, ly = wy & 31;
            ushort[]? chunk = null;
            int chunkX = int.MinValue;
            for (int px = 0; px < TileSize; px++)
            {
                int wx = (int)Math.Floor(tx * span + (px + 0.5) / perTile);
                int cx = wx >> 5;
                if (cx != chunkX)
                {
                    chunkX = cx;
                    surface.Chunks.TryGetValue((cx, cy), out chunk);
                }
                if (chunk is not null)
                    pixels[py * TileSize + px] = colors[chunk[ly * 32 + (wx & 31)]];
            }
        }
        var bitmap = new SKBitmap(new SKImageInfo(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    private static SKBitmap DrawShapes(List<Shape> shapes, List<int> indexes, int tx, int ty, double span, GameData game,
        ConcurrentDictionary<string, SKImage?> icons)
    {
        var bitmap = new SKBitmap(new SKImageInfo(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        float perTile = (float)(TileSize / span);
        canvas.Scale(perTile);
        canvas.Translate((float)(-tx * span), (float)(-ty * span));
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.4f / perTile, 0.08f), StrokeCap = SKStrokeCap.Round };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        foreach (int i in indexes)
        {
            var s = shapes[i];
            if (s.Kind == ShapeKind.Line)
            {
                line.Color = s.Color;
                canvas.DrawLine(s.X1, s.Y1, s.X2, s.Y2, line);
                continue;
            }
            fill.Color = s.Color;
            canvas.DrawRect(s.X1, s.Y1, s.X2 - s.X1, s.Y2 - s.Y1, fill);
            // the building's icon, once it is large enough to be recognised
            float size = Math.Min(s.X2 - s.X1, s.Y2 - s.Y1) * 0.72f;
            if (s.Icon is not null && size * perTile >= 14 && icons.GetOrAdd(s.Icon, key => LoadIcon(game, key)) is { } icon)
            {
                float cx = (s.X1 + s.X2) / 2, cy = (s.Y1 + s.Y2) / 2;
                canvas.DrawImage(icon, SKRect.Create(cx - size / 2, cy - size / 2, size, size), sampling);
            }
        }
        return bitmap;
    }

    private static SKImage? LoadIcon(GameData game, string key) => Load(game.IconFile(key));

    /// <summary>Decoded once into memory: an image straight from its file would be decoded again on every draw.</summary>
    private static SKImage? Load(string file)
    {
        if (!File.Exists(file))
            return null;
        using var encoded = SKImage.FromEncodedData(file);
        return encoded?.ToRasterImage(ensurePixelData: true);
    }

    private static SKBitmap Downsample(string layerDirectory, int childZoom, int px, int py, HashSet<(int X, int Y)> children)
    {
        var bitmap = new SKBitmap(new SKImageInfo(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        for (int dx = 0; dx < 2; dx++)
        for (int dy = 0; dy < 2; dy++)
        {
            int cx = px * 2 + dx, cy = py * 2 + dy;
            if (!children.Contains((cx, cy)))
                continue;
            using var child = SKImage.FromEncodedData(TilePath(layerDirectory, childZoom, cx, cy));
            if (child is not null)
                canvas.DrawImage(child, SKRect.Create(dx * TileSize / 2f, dy * TileSize / 2f, TileSize / 2f, TileSize / 2f), sampling);
        }
        return bitmap;
    }

    private static string TilePath(string layerDirectory, int z, int x, int y) =>
        Path.Combine(layerDirectory, z.ToString(), x.ToString(), y + ".png");

    private static void Save(SKBitmap bitmap, string layerDirectory, int z, int x, int y)
    {
        string path = TilePath(layerDirectory, z, x, y);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 4))
            ?? throw new InvalidOperationException($"Could not encode the tile {path}.");
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
