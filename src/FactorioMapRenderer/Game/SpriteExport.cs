using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace FactorioMapRenderer.Game;

/// <summary>Where a sprite lies around its entity's position, in tiles.</summary>
public readonly record struct SpriteBox(float Left, float Top, float Width, float Height);

/// <summary>
/// A ground texture: <see cref="Count"/> variants of one tile side by side, or (<see cref="Material"/>) one texture
/// of <see cref="Width"/> × <see cref="Height"/> tiles that repeats over the ground.
/// </summary>
public readonly record struct TileTexture(bool Material, int Count, int Width, int Height);

/// <summary>
/// The entities as the game draws them, for the "game graphics" layer: one image per entity and direction (and per
/// shape for pipes, per end for underground belts), cut from the game's sprite sheets at their full resolution, 64 px per tile.
/// </summary>
/// <remarks>
/// Factorio describes graphics in many forms: a sprite, its layers, one per direction (north/east/south/west), a
/// sheet with the directions side by side, animations whose frames lie in rows, rotated sprites with many
/// directions. Each is resolved to frame 0 of the wanted direction; shadows and lights are left out.
/// </remarks>
internal static class SpriteExport
{
    public const int PixelsPerTile = 64;

    /// <summary>Variant keys: the direction 0, 4, 8, 12 (north, east, south, west), "in-4", "pipe-cross" …</summary>
    public static readonly string[] Directions = ["0", "4", "8", "12"];
    private static readonly string[] DirectionNames = ["north", "east", "south", "west"];

    /// <summary>
    /// Not drawn in this layer: rail ramps are pieces the game puts together, vehicles and creatures turn freely.
    /// </summary>
    private static readonly HashSet<string> SkippedTypes =
    [
        "rail-ramp", "car", "spider-vehicle", "locomotive", "cargo-wagon", "fluid-wagon", "artillery-wagon", "infinity-cargo-wagon",
        "character", "unit", "spider-unit", "segmented-unit", "segment", "fish",
        "cargo-pod", "spider-leg", "rocket-silo-rocket", "rocket-silo-rocket-shadow", "asteroid",
        // remains, effects and decoration, never part of the factory
        "corpse", "character-corpse", "rail-remnants", "optimized-decorative", "explosion", "projectile", "artillery-projectile",
        "fire", "sticker", "stream", "beam", "smoke-with-trigger", "particle-source", "highlight-box", "speech-bubble", "arrow",
        "artillery-flare", "item-entity", "entity-ghost", "tile-ghost", "item-request-proxy", "deconstructible-tile-proxy",
        "combat-robot", "construction-robot", "logistic-robot", "capture-robot", "lightning", "temporary-container", "simple-entity-with-owner",
    ];

    /// <summary>Where the main picture of most entities is, in the order tried.</summary>
    private static readonly string[] MainPaths =
    [
        "graphics_set.animation", "graphics_set.idle_animation", "graphics_set.picture", "graphics_set.structure",
        "graphics_set.base_visualisation.animation", "graphics_set.animation_list.0.animation",
        "structure", "animation", "idle_animation", "picture", "pictures", "sprites", "base_picture", "base", "on_animation",
        "chargable_graphics.picture", "picture_off", "folded_animation", "animations", "base_day_sprite", "power_on_animation",
        "sprite", "platform_picture", "frozen_patch", "stateless_visualisation.animation",
    ];

    /// <summary>Drawn below the main picture: the ground under a machine, the part of a splitter behind its belts.</summary>
    private static readonly string[] UnderPaths = ["integration_patch", "graphics_set.integration_patch", "structure_patch"];

    /// <summary>
    /// The parts of a rail, as the game lays them: the beds of all rails first, then their ties, then their metals, so
    /// that rails that cross or join look whole. Variant "{direction}-{part}", for the eight directions 0, 2 … 14.
    /// </summary>
    public static readonly string[] RailParts = ["bed", "ties", "metals"];
    private static readonly string[][] RailPieces = [["stone_path_background", "stone_path"], ["ties"], ["backplates", "metals"]];
    private static readonly string[] RailDirections = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"];

    /// <summary>Trees, rocks and ore come in variations; the map picks one by position.</summary>
    public const int Variations = 4;

    /// <summary>Pipe shapes by their connections (up, right, down, left as bits 1, 2, 4, 8).</summary>
    public static readonly string[] PipeShapes =
    [
        "straight_vertical_single", "ending_up", "ending_right", "corner_up_right", "ending_down", "straight_vertical", "corner_down_right", "t_right",
        "ending_left", "corner_up_left", "straight_horizontal", "t_up", "corner_down_left", "t_left", "t_down", "cross",
    ];

    /// <summary>Renders every entity's sprites into <paramref name="directory"/>; returns name → variant → box.</summary>
    public static SortedDictionary<string, SortedDictionary<string, SpriteBox>> Export(JsonObject dump, string dataDirectory, string directory, Action<string> log)
    {
        var result = new SortedDictionary<string, SortedDictionary<string, SpriteBox>>(StringComparer.Ordinal);
        var images = new Dictionary<string, SKBitmap?>(StringComparer.Ordinal);
        int failed = 0;
        foreach (var (type, entries) in dump)
        {
            if (SkippedTypes.Contains(type) || entries is not JsonObject prototypes)
                continue;
            foreach (var (name, node) in prototypes)
            {
                if (node is not JsonObject prototype || !(prototype.ContainsKey("collision_box") || prototype.ContainsKey("selection_box")))
                    continue;
                if (prototype.ContainsKey("stack_size"))
                    continue;
                try
                {
                    var variants = Variants(type, prototype);
                    var boxes = new SortedDictionary<string, SpriteBox>(StringComparer.Ordinal);
                    string? previous = null;
                    foreach (var (key, layers) in variants)
                    {
                        if (layers.Count == 0)
                            continue;
                        // a picture that is the same in every direction is kept once, as direction 0
                        string signature = string.Join("|", layers);
                        if (Directions.Contains(key) && key != "0" && signature == previous)
                            continue;
                        if (key == "0")
                            previous = signature;
                        if (Render(layers, dataDirectory, images, Path.Combine(directory, name, key + ".png")) is { } box)
                            boxes[key] = box;
                    }
                    if (boxes.Count > 0)
                        result[name] = boxes;
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or NullReferenceException)
                {
                    failed++;
                }
            }
        }
        foreach (var image in images.Values)
            image?.Dispose();
        log($"  sprites of {result.Count:N0} entities{(failed > 0 ? $" ({failed} could not be read)" : "")}");
        return result;
    }

    /// <summary>The ground textures of every tile, at 64 px per tile, into <paramref name="directory"/>.</summary>
    public static SortedDictionary<string, TileTexture> ExportTiles(JsonObject dump, string dataDirectory, string directory, Action<string> log)
    {
        var result = new SortedDictionary<string, TileTexture>(StringComparer.Ordinal);
        var images = new Dictionary<string, SKBitmap?>(StringComparer.Ordinal);
        foreach (var (name, node) in dump["tile"] as JsonObject ?? [])
        {
            if (node?["variants"] is not JsonObject variants)
                continue;
            // opaque ground, so JPEG: a tenth of the size at the same look
            string target = System.IO.Path.Combine(directory, name + ".jpg");
            // a texture that repeats over the ground (concrete, landfill, …)
            if (variants["material_background"] is JsonObject material && (string?)material["picture"] is { } picture
                && Load(picture, dataDirectory, images) is { } texture)
            {
                float scale = material["scale"] is { } sc ? (float)(double)sc : 1;
                int w = Math.Max(1, (int)Math.Round(texture.Width * scale / 32)), h = Math.Max(1, (int)Math.Round(texture.Height * scale / 32));
                if (Save(texture, SKRect.Create(0, 0, texture.Width, texture.Height), w * PixelsPerTile, h * PixelsPerTile, target))
                    result[name] = new TileTexture(true, 1, w, h);
                continue;
            }
            // variants of one tile in a row (grass, sand, water, platform foundation, …)
            var main = (variants["main"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var single = main.FirstOrDefault(m => m["size"] is null || (double)m["size"]! == 1) ?? main.FirstOrDefault();
            if (single is null || (string?)single["picture"] is not { } file || Load(file, dataDirectory, images) is not { } strip)
                continue;
            float cellScale = single["scale"] is { } cs ? (float)(double)cs : 1;
            int cell = (int)Math.Round(32 / cellScale);
            int count = Math.Clamp(single["count"] is { } c ? (int)(double)c : 1, 1, 16);
            int line = single["line_length"] is { } ll && (int)(double)ll > 0 ? (int)(double)ll : count;
            int y0 = single["y"] is { } y ? (int)(double)y : 0;
            count = Math.Min(count, Math.Min(line, strip.Width / Math.Max(cell, 1)));
            if (count < 1 || y0 + cell > strip.Height)
                continue;
            if (Save(strip, SKRect.Create(0, y0, count * cell, cell), count * PixelsPerTile, PixelsPerTile, target))
                result[name] = new TileTexture(false, count, 1, 1);
        }
        foreach (var image in images.Values)
            image?.Dispose();
        log($"  ground textures of {result.Count:N0} tiles");
        return result;
    }

    private static bool Save(SKBitmap source, SKRect from, int width, int height, string target)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, from, SKRect.Create(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        using var stream = File.Create(target);
        return bitmap.Encode(stream, SKEncodedImageFormat.Jpeg, 92);
    }

    /// <summary>
    /// Draws one picture (a utility sprite such as the sun of the star map) at its own resolution into
    /// <paramref name="target"/>; returns its size in pixels, or null if there is none.
    /// </summary>
    public static (int Width, int Height)? ExportPicture(JsonNode? node, string dataDirectory, string target)
    {
        var layers = Layers(node, 0);
        if (layers.Count == 0)
            return null;
        // at the picture's own resolution instead of the map's 64 px per tile
        float factor = 32f / PixelsPerTile / Math.Max(layers.Min(l => l.Scale), 0.01f);
        layers = layers.Select(l => l with { Scale = l.Scale * factor }).ToList();
        var images = new Dictionary<string, SKBitmap?>(StringComparer.Ordinal);
        var box = Render(layers, dataDirectory, images, target);
        foreach (var image in images.Values)
            image?.Dispose();
        return box is { } b ? ((int)Math.Round(b.Width * PixelsPerTile), (int)Math.Round(b.Height * PixelsPerTile)) : null;
    }

    /// <summary>A picture file as it is, e.g. a planet in the star map (512 px).</summary>
    public static bool CopyPicture(string path, string dataDirectory, string target)
    {
        var images = new Dictionary<string, SKBitmap?>(StringComparer.Ordinal);
        try
        {
            if (Load(path, dataDirectory, images) is not { } image)
                return false;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            using var stream = File.Create(target);
            return image.Encode(stream, SKEncodedImageFormat.Png, 100);
        }
        finally
        {
            foreach (var image in images.Values)
                image?.Dispose();
        }
    }

    /// <summary>The layers to draw for each variant of an entity.</summary>
    private static List<(string Key, List<Layer> Layers)> Variants(string type, JsonObject p)
    {
        var variants = new List<(string, List<Layer>)>();
        switch (type)
        {
            case "transport-belt":
            {
                // the belt sheet has a row per direction of travel: east, west, north, south, then the curves
                var set = Find(p, "belt_animation_set.animation_set");
                int[] rows = [2, 0, 3, 1];
                for (int d = 0; d < 4; d++)
                    variants.Add((Directions[d], set is null ? [] : Layers(set, d, frame: rows[d] * FrameCount(set))));
                return variants;
            }
            case "underground-belt" or "loader" or "loader-1x1":
                for (int d = 0; d < 4; d++)
                {
                    variants.Add(($"in-{Directions[d]}", Belt(p, d).Concat(Layers(Find(p, "structure.direction_in"), d)).ToList()));
                    variants.Add(($"out-{Directions[d]}", Belt(p, d).Concat(Layers(Find(p, "structure.direction_out"), d)).ToList()));
                }
                return variants;
            case "pipe":
                for (int mask = 0; mask < 16; mask++)
                    variants.Add(($"pipe-{mask}", Layers(Find(p, $"pictures.{PipeShapes[mask]}"), 0)));
                return variants;
            case "gate":
                for (int d = 0; d < 4; d++)
                    variants.Add((Directions[d], Layers(Find(p, d % 2 == 0 ? "vertical_animation" : "horizontal_animation"), d)));
                return variants;
            case "straight-rail" or "curved-rail-a" or "curved-rail-b" or "half-diagonal-rail" or "legacy-straight-rail" or "legacy-curved-rail"
                or "elevated-straight-rail" or "elevated-curved-rail-a" or "elevated-curved-rail-b" or "elevated-half-diagonal-rail":
                for (int d = 0; d < RailDirections.Length; d++)
                    if (Find(p, $"pictures.{RailDirections[d]}") is JsonObject piece)
                        for (int part = 0; part < RailParts.Length; part++)
                            variants.Add(($"{d * 2}-{RailParts[part]}", RailPieces[part].SelectMany(k => Layers(piece[k], 0)).ToList()));
                return variants;
            case "rail-signal" or "rail-chain-signal":
                // the post in each of 16 directions, green; a row of frames per direction
                if (Find(p, "ground_picture_set.structure") is { } structure && Layers(structure, 0).Count > 0)
                {
                    int frames = FrameCount(structure["layers"]?[0] ?? structure);
                    for (int d = 0; d < 16; d++)
                        variants.Add((d.ToString(System.Globalization.CultureInfo.InvariantCulture), Layers(structure, 0, frame: d * frames)));
                }
                return variants;
            case "cliff":
                // a picture per orientation ("west_to_east" …), in variations, each over its lower part
                if (p["orientations"] is JsonObject orientations)
                    foreach (var (orientation, o) in orientations)
                        if (o?["pictures"] is JsonArray sides)
                            for (int v = 0; v < Math.Min(sides.Count, Variations); v++)
                                variants.Add(($"{orientation}-v{v}", Layers((o["pictures_lower"] as JsonArray)?.ElementAtOrDefault(v), 0)
                                    .Concat(Layers(sides[v], 0)).ToList()));
                return variants;
            case "inserter":
                // the game draws the arm itself, over the buildings around: from the middle of the platform to the
                // hand, here waiting over the pickup position, which turns with the inserter; "{direction}-arm"
                for (int d = 0; d < 4; d++)
                {
                    var arm = new List<Layer>();
                    var (px, py) = p["pickup_position"] is JsonArray pick && pick.Count >= 2 ? ((float)(double)pick[0]!, (float)(double)pick[1]!) : (0f, -1f);
                    for (int r = 0; r < d; r++)
                        (px, py) = (-py, px);
                    float length = MathF.Sqrt(px * px + py * py), angle = MathF.Atan2(px, -py) * 180 / MathF.PI;
                    foreach (var segment in Layers(p["hand_base_picture"], 0))
                        arm.Add(segment with { ShiftX = px / 2, ShiftY = py / 2, Angle = angle, Stretch = length / Math.Max(segment.Height * segment.Scale / 32f, 0.01f) });
                    foreach (var hand in Layers(p["hand_open_picture"], 0))
                        arm.Add(hand with { ShiftX = px, ShiftY = py, Angle = angle });
                    variants.Add((Directions[d], Layers(p["platform_picture"], d)));
                    variants.Add(($"{Directions[d]}-{ArmVariant}", arm));
                }
                return variants;
            case "rocket-silo":
                // the doors closed over the hole, under the silo's ring
                variants.Add(("0", new[] { "hole_sprite", "door_back_sprite", "door_front_sprite", "base_day_sprite", "base_front_sprite" }
                    .SelectMany(part => Layers(p[part], 0)).ToList()));
                return variants;
            case "ammo-turret" or "electric-turret" or "fluid-turret" or "turret" when p["folded_animation"] is { } head:
            {
                // the base, and the head folded up, looking the turret's way
                var bases = Find(p, "graphics_set.base_visualisation") switch
                {
                    JsonArray list => list.Select(b => b?["animation"]).ToList(),
                    JsonObject one => [one["animation"]],
                    _ => [p["base_picture"]],
                };
                for (int d = 0; d < 4; d++)
                    variants.Add((Directions[d], bases.SelectMany(b => Layers(b, d)).Concat(Layers(head, d)).ToList()));
                return variants;
            }
            case "wall":
                variants.Add(("0", Layers(Find(p, "pictures.single"), 0)));
                return variants;
            case "resource":
            {
                // a column per stage (richest first), a row per variation; geysers and vents have their stages as layers
                var picture = Find(p, "stages.sheet") ?? Find(p, "stages");
                if ((picture?["layers"]?[0] ?? picture) is not JsonObject sheet)
                    return variants;
                int stages = FrameCount(sheet), rows = sheet["variation_count"] is { } vc ? (int)(double)vc : 1;
                for (int stage = 0; stage < stages; stage++)
                    for (int v = 0; v < Math.Min(rows, Variations); v++)
                        variants.Add(($"s{stage}-v{v}", Layers(picture, 0, frame: v * stages + stage)));
                return variants;
            }
            case "tree" or "plant":
                if (p["variations"] is JsonArray trees)
                {
                    for (int v = 0; v < Math.Min(trees.Count, Variations); v++)
                        variants.Add(($"v{v}", Layers(trees[v]?["trunk"], 0).Concat(Layers(trees[v]?["leaves"], 0)).ToList()));
                    return variants;
                }
                break;
        }
        if (type is "simple-entity" or "tree" or "plant" && (p["pictures"] ?? p["animations"]) is JsonArray pictures)
        {
            for (int v = 0; v < Math.Min(pictures.Count, Variations); v++)
                variants.Add(($"v{v}", Layers(pictures[v], 0)));
            return variants;
        }
        var main = MainPaths.Select(path => Find(p, path)).FirstOrDefault(n => n is not null && Layers(n, 0).Count > 0)
            ?? FirstSprite(p["graphics_set"]) ?? FirstSprite(p);
        // hubs, landing pads and cargo bays: the picture in its render layers, and the animation over it
        JsonNode? over = null;
        if (Find(p, "graphics_set.picture") is JsonArray hub && RenderLayers(hub))
            (main, over) = (hub, Find(p, "graphics_set.animation"));
        for (int d = 0; d < 4; d++)
        {
            var layers = new List<Layer>();
            foreach (string path in UnderPaths)
                layers.AddRange(Layers(Find(p, path), d));
            // splitters stand on belts
            if (type is "splitter" or "lane-splitter")
                layers.AddRange(Belt(p, d, sideways: true));
            layers.AddRange(Layers(main, d));
            layers.AddRange(Layers(over, d));
            // parts the game always draws on top, like the body of an electric mining drill; not the status lights
            if (Find(p, "graphics_set.working_visualisations") is JsonArray visualisations)
                foreach (var v in visualisations.OfType<JsonObject>())
                    if (Bool(v, "always_draw") && (string?)v["apply_tint"] != "status" && !Bool(v, "draw_as_light"))
                        layers.AddRange(Layers(v[$"{DirectionNames[d]}_animation"] ?? v["animation"], d));
            variants.Add((Directions[d], layers));
        }
        // cargo bays look different on a space platform
        if (Find(p, "platform_graphics_set.picture") is { } platform)
            variants.Add((PlatformVariant, Layers(platform, 0)));
        return variants;
    }

    /// <summary>An inserter's arm and hand, drawn over the buildings: variant "{direction}-arm".</summary>
    public const string ArmVariant = "arm";

    /// <summary>The variant of an entity on a space platform, where the game has a picture of its own for it.</summary>
    public const string PlatformVariant = "platform";

    /// <summary>The belt under an underground belt, loader or splitter, in its direction.</summary>
    private static IEnumerable<Layer> Belt(JsonObject p, int direction, bool sideways = false)
    {
        var set = Find(p, "belt_animation_set.animation_set");
        if (set is null)
            return [];
        int[] rows = [2, 0, 3, 1];
        var layers = Layers(set, direction, frame: rows[direction] * FrameCount(set));
        if (!sideways)
            return layers;
        // a splitter is two belts wide, across its direction
        float dx = direction % 2 == 0 ? 0.5f : 0, dy = direction % 2 == 0 ? 0 : 0.5f;
        return layers.Select(l => l with { ShiftX = l.ShiftX - dx, ShiftY = l.ShiftY - dy })
            .Concat(layers.Select(l => l with { ShiftX = l.ShiftX + dx, ShiftY = l.ShiftY + dy }));
    }

    /// <summary>Keys that hold no picture of the entity itself.</summary>
    private static readonly string[] NotPictures = ["shadow", "light", "reflection", "icon", "remnants", "corpse", "damaged", "circuit", "visualisation", "glow", "explosion", "sound", "smoke", "working"];

    /// <summary>The first thing that looks like a picture, for entities whose graphics lie where no other's do.</summary>
    private static JsonNode? FirstSprite(JsonNode? node, int depth = 0)
    {
        if (node is not JsonObject o || depth > 4)
            return null;
        foreach (var (key, value) in o)
        {
            if (NotPictures.Any(k => key.Contains(k, StringComparison.Ordinal)) || value is null)
                continue;
            var candidate = value is JsonArray a && a.Count > 0 ? a[0] : value;
            if (candidate is JsonObject c && new[] { "filename", "filenames", "stripes", "layers", "sheet", "sheets", "north" }.Any(c.ContainsKey))
                return value;
            if (FirstSprite(candidate, depth + 1) is { } inner)
                return inner;
        }
        return null;
    }

    private static int FrameCount(JsonNode node) => node["frame_count"] is { } f ? (int)(double)f : 1;

    private static JsonNode? Find(JsonNode node, string path)
    {
        JsonNode? current = node;
        foreach (string part in path.Split('.'))
            current = current is JsonArray a && int.TryParse(part, out int i) ? (i < a.Count ? a[i] : null) : current is JsonObject o ? o[part] : null;
        return current;
    }

    /// <summary>One image to draw: a part of a file, its size in the file, its scale and shift.</summary>
    /// <param name="Angle">Turned clockwise about its middle, in degrees: an inserter's arm.</param>
    /// <param name="Stretch">Drawn this much taller: an arm as long as it reaches.</param>
    private sealed record Layer(string File, int X, int Y, int Width, int Height, float Scale, float ShiftX, float ShiftY, SKColor? Tint,
        float Angle = 0, float Stretch = 1)
    {
        public override string ToString() => $"{File}:{X},{Y},{Width},{Height},{Scale},{ShiftX},{ShiftY},{Tint},{Angle},{Stretch}";
    }

    /// <summary>Resolves any graphics definition to the layers of frame 0 in direction <paramref name="direction"/> (0–3).</summary>
    private static List<Layer> Layers(JsonNode? node, int direction, int frame = -1)
    {
        var result = new List<Layer>();
        Collect(node, direction, frame, result);
        return result;
    }

    /// <param name="sheet">A sheet: the four directions in one row, the frame is the direction.</param>
    private static void Collect(JsonNode? node, int direction, int frame, List<Layer> into, bool sheet = false)
    {
        switch (node)
        {
            case null:
                return;
            case JsonArray groups when RenderLayers(groups):
                // one picture in parts the game draws on different render layers (the platform hub, cargo bays): all of them
                foreach (var group in groups)
                    Collect(group, direction, frame, into, sheet);
                return;
            case JsonArray variations:
                // variations of the same picture: the first
                if (variations.Count > 0)
                    Collect(variations[0], direction, frame, into, sheet);
                return;
            case not JsonObject:
                return;
        }
        var o = (JsonObject)node;
        if (o["layers"] is JsonArray layers)
        {
            foreach (var layer in layers)
                Collect(layer, direction, frame, into, sheet);
            return;
        }
        if (DirectionNames.Any(o.ContainsKey))
        {
            Collect(o[DirectionNames[direction]] ?? o["north"], direction, frame, into);
            return;
        }
        // the four directions side by side in one file
        if (o["sheet"] is { } single)
        {
            Collect(single, direction, direction, into, sheet: true);
            return;
        }
        if (o["sheets"] is JsonArray sheets)
        {
            foreach (var s in sheets)
                Collect(s, direction, direction, into, sheet: true);
            return;
        }
        if (!o.ContainsKey("filename") && !o.ContainsKey("filenames") && !o.ContainsKey("stripes"))
        {
            // a picture set: boiler { structure }, storage tank { picture }, …
            foreach (string inner in new[] { "structure", "picture", "animation", "sprite" })
                if (o[inner] is { } nested)
                {
                    Collect(nested, direction, frame, into, sheet);
                    return;
                }
            return;
        }
        if (Bool(o, "draw_as_shadow") || Bool(o, "draw_as_light") || (string?)o["blend_mode"] is "additive" or "additive-soft")
            return;

        string? file = (string?)o["filename"] ?? (o["filenames"] as JsonArray)?[0]?.GetValue<string>() ?? (string?)(o["stripes"] as JsonArray)?[0]?["filename"];
        if (file is null)
            return;
        (int width, int height) = o["size"] switch
        {
            JsonArray s when s.Count >= 2 => ((int)(double)s[0]!, (int)(double)s[1]!),
            JsonValue s => ((int)(double)s, (int)(double)s),
            _ => ((int)(double)(o["width"] ?? 0), (int)(double)(o["height"] ?? 0)),
        };
        if (width <= 0 || height <= 0)
            return;
        int x = o["x"] is { } ox ? (int)(double)ox : o["position"] is JsonArray pos ? (int)(double)pos[0]! : 0;
        int y = o["y"] is { } oy ? (int)(double)oy : o["position"] is JsonArray pos2 ? (int)(double)pos2[1]! : 0;
        // the frame: for a rotated sprite the direction picks it (4 directions: 0, ¼, ½, ¾ of them)
        int index = frame;
        if (index < 0)
            index = o["direction_count"] is { } dc && (int)(double)dc >= 4 ? direction * (int)(double)dc / 4 * Math.Max(1, FrameCount(o)) : 0;
        if (o["direction_count"] is { } count && (int)(double)count == 1)
            index = 0;
        int frames = Math.Max(FrameCount(o), o["direction_count"] is { } d2 ? (int)(double)d2 * FrameCount(o) : 1);
        // without a line length an animation's frames lie in one row, a rotated animation has a row per direction
        int line = o["line_length"] is { } ll && (int)(double)ll > 0 ? (int)(double)ll
            : sheet ? (o["frames"] is { } f ? (int)(double)f : 4)
            : o.ContainsKey("direction_count") && FrameCount(o) > 1 ? FrameCount(o)
            : frames;
        x += index % Math.Max(line, 1) * width;
        y += index / Math.Max(line, 1) * height;
        float scale = o["scale"] is { } sc ? (float)(double)sc : 1;
        float sx = 0, sy = 0;
        if (o["shift"] is JsonArray shift && shift.Count >= 2)
            (sx, sy) = ((float)(double)shift[0]!, (float)(double)shift[1]!);
        else if (o["shift"] is JsonObject so)
            (sx, sy) = ((float)(double)(so["x"] ?? 0), (float)(double)(so["y"] ?? 0));
        into.Add(new Layer(file, x, y, width, height, scale, sx, sy, o["tint"] is { } tint ? GameData.ToColor(tint) : null));
    }

    /// <summary>One picture in parts, each for a render layer of its own, rather than variations of it.</summary>
    private static bool RenderLayers(JsonArray groups) => groups.Count > 0 && groups.All(g => g is JsonObject o && o.ContainsKey("render_layer"));

    private static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out bool b) && b;

    /// <summary>Draws the layers into one image around the entity's position; returns where it lies.</summary>
    private static SpriteBox? Render(List<Layer> layers, string dataDirectory, Dictionary<string, SKBitmap?> images, string target)
    {
        // each layer's rectangle in tiles around the position
        var boxes = layers.Select(l =>
        {
            float w = l.Width * l.Scale / 32f, h = l.Height * l.Scale / 32f * l.Stretch;
            return SKRect.Create(l.ShiftX - w / 2, l.ShiftY - h / 2, w, h);
        }).ToList();
        // a turned layer reaches as far as its turned corners
        var bounds = boxes.Select((b, i) =>
        {
            if (layers[i].Angle == 0)
                return b;
            float a = layers[i].Angle * MathF.PI / 180, c = MathF.Abs(MathF.Cos(a)), s = MathF.Abs(MathF.Sin(a));
            float hw = (b.Width * c + b.Height * s) / 2, hh = (b.Width * s + b.Height * c) / 2;
            return new SKRect(b.MidX - hw, b.MidY - hh, b.MidX + hw, b.MidY + hh);
        }).ToList();
        float left = bounds.Min(b => b.Left), top = bounds.Min(b => b.Top), right = bounds.Max(b => b.Right), bottom = bounds.Max(b => b.Bottom);
        int pw = (int)Math.Ceiling((right - left) * PixelsPerTile), ph = (int)Math.Ceiling((bottom - top) * PixelsPerTile);
        if (pw <= 0 || ph <= 0 || pw > 4096 || ph > 4096)
            return null;
        using var bitmap = new SKBitmap(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        bool drawn = false;
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        for (int i = 0; i < layers.Count; i++)
        {
            var l = layers[i];
            if (Load(l.File, dataDirectory, images) is not { } source)
                continue;
            var src = SKRect.Create(l.X, l.Y, l.Width, l.Height);
            if (src.Right > source.Width || src.Bottom > source.Height)
                continue;
            var b = boxes[i];
            var dest = SKRect.Create((b.Left - left) * PixelsPerTile, (b.Top - top) * PixelsPerTile, b.Width * PixelsPerTile, b.Height * PixelsPerTile);
            using var paint = new SKPaint { IsAntialias = true };
            if (l.Tint is { } tint)
                paint.ColorFilter = SKColorFilter.CreateBlendMode(tint.WithAlpha(255), SKBlendMode.Modulate);
            using var image = SKImage.FromBitmap(source);
            canvas.Save();
            canvas.RotateDegrees(l.Angle, dest.MidX, dest.MidY);
            canvas.DrawImage(image, src, dest, sampling, paint);
            canvas.Restore();
            drawn = true;
        }
        if (!drawn)
            return null;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        using (var stream = File.Create(target))
            bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        return new SpriteBox(left, top, pw / (float)PixelsPerTile, ph / (float)PixelsPerTile);
    }

    /// <summary>Sheets are large (up to 4096 px square); only the last few are kept decoded.</summary>
    private const int CachedImages = 12;

    private static SKBitmap? Load(string path, string dataDirectory, Dictionary<string, SKBitmap?> cache)
    {
        if (cache.TryGetValue(path, out var cached))
            return cached;
        if (cache.Count >= CachedImages)
        {
            foreach (var image in cache.Values)
                image?.Dispose();
            cache.Clear();
        }
        var m = Regex.Match(path, "^__([^/]+?)__/(.+)$");
        string file = m.Success ? System.IO.Path.Combine(dataDirectory, m.Groups[1].Value, m.Groups[2].Value) : System.IO.Path.Combine(dataDirectory, path);
        var bitmap = File.Exists(file) ? SKBitmap.Decode(file) : null;
        cache[path] = bitmap;
        return bitmap;
    }
}
