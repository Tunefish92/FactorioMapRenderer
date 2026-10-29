using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace FactorioMapRenderer.Game;

/// <summary>The colours of Factorio's map view, from the game's utility constants ("chart").</summary>
public sealed class ChartColors
{
    public SKColor DefaultFriendly { get; init; } = new(0, 97, 145);
    public SKColor DefaultEnemy { get; init; } = new(255, 25, 25);
    public SKColor Rail { get; init; } = new(140, 140, 140);
    public SKColor ElevatedRail { get; init; } = new(186, 186, 186);
    public SKColor RailRamp { get; init; } = new(166, 166, 166);
    public SKColor ElectricPole { get; init; } = new(0, 158, 163);
    public SKColor CopperWire { get; init; } = new(0, 212, 255);
    public IReadOnlyDictionary<string, SKColor> FriendlyByType { get; init; } = new Dictionary<string, SKColor>();
    public IReadOnlyDictionary<string, SKColor> ByType { get; init; } = new Dictionary<string, SKColor>();
}

/// <summary>
/// What the map needs of the game besides the save: names, icons and the map view's colours. Exported once from a
/// game installation (see <see cref="Export"/>) into a folder that the renderer reads from then on; the image
/// brings such a folder, so no game is needed for icons.
/// </summary>
public sealed class GameData
{
    public const string DataFileName = "gamedata.json";
    /// <summary>Raised when the exported layout changes, so that an older export is made again.</summary>
    private const int FormatVersion = 2;

    public string Directory { get; }
    public string GameVersion { get; }
    public ChartColors Chart { get; }

    private readonly Dictionary<string, string> _names;
    private readonly HashSet<string> _icons;
    private readonly Dictionary<string, string> _entityIcons;
    private readonly Dictionary<string, Dictionary<string, SpriteBox>> _sprites;
    private readonly Dictionary<string, TileTexture> _tiles;

    private GameData(string directory, string gameVersion, ChartColors chart, Dictionary<string, string> names, HashSet<string> icons,
        Dictionary<string, string> entityIcons, Dictionary<string, Dictionary<string, SpriteBox>> sprites, Dictionary<string, TileTexture> tiles)
    {
        (Directory, GameVersion, Chart, _names, _icons, _entityIcons, _sprites, _tiles) = (directory, gameVersion, chart, names, icons, entityIcons, sprites, tiles);
    }

    /// <summary>Without exported game data: readable names from the ids, the default colours, no icons.</summary>
    public static GameData Empty { get; } = new("", "", new ChartColors(), [], [], [], [], []);

    /// <summary>The ground texture of a tile, if the game data has one.</summary>
    public TileTexture? Tile(string tile) => _tiles.TryGetValue(tile, out var texture) ? texture : null;

    public string TileFile(string tile) => Path.Combine(Directory, "tiles", tile + ".jpg");

    /// <summary>The amounts from which a resource shows its richer pictures, richest first ("stage_counts").</summary>
    public IReadOnlyList<long> ResourceStages(string resource) => _stages.TryGetValue(resource, out var stages) ? stages : [];

    private Dictionary<string, long[]> _stages = [];

    public bool HasGraphics => _sprites.Count > 0 || _tiles.Count > 0;

    /// <summary>The solar system as the star map shows it; null without exported game data.</summary>
    public JsonObject? Space { get; private init; }

    public string SpaceFile(string name) => Path.Combine(Directory, "space", name + ".png");

    /// <summary>
    /// The entity as the game draws it: the key of its image and where that lies around the entity's position. The
    /// variant is the direction ("0", "4", "8", "12"), "in-…"/"out-…" for underground belts, "pipe-N" for pipe shapes;
    /// a picture that is the same in every direction is only there as "0".
    /// </summary>
    public (string Key, SpriteBox Box)? Sprite(string entity, string variant, bool exact = false)
    {
        if (!_sprites.TryGetValue(entity, out var variants))
            return null;
        if (variants.TryGetValue(variant, out var box) || !exact && variants.TryGetValue(variant = "0", out box))
            return ($"{entity}/{variant}", box);
        return null;
    }

    public string SpriteFile(string key) => Path.Combine(Directory, "sprites", key + ".png");

    public string? LogoFile => File.Exists(Path.Combine(Directory, "logo.png")) ? Path.Combine(Directory, "logo.png") : null;

    /// <summary>"entity", "item", "fluid", "recipe", "space-location", "virtual-signal", "technology", "tile", "quality".</summary>
    public string Name(string kind, string id)
    {
        if (_names.TryGetValue($"{kind}/{id}", out string? name))
            return name;
        // Entities are often named after the item that places them, and the other way round.
        if (kind == "entity" && _names.TryGetValue($"item/{id}", out name) || kind == "item" && _names.TryGetValue($"entity/{id}", out name))
            return name;
        if (kind == "planet" && _names.TryGetValue($"space-location/{id}", out name))
            return name;
        // Most recipes are named after what they make.
        if (kind == "recipe" && (_names.TryGetValue($"item/{id}", out name) || _names.TryGetValue($"fluid/{id}", out name)))
            return name;
        return Readable(id);
    }

    /// <summary>"assembling-machine-2" → "Assembling machine 2".</summary>
    public static string Readable(string id)
    {
        string text = id.Replace('-', ' ').Replace('_', ' ').Trim();
        return text.Length == 0 ? id : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Key of the icon ("item/iron-plate") for something of this kind, or null when there is none.</summary>
    public string? IconKey(string kind, string id)
    {
        if (kind == "entity" && _entityIcons.TryGetValue(id, out string? key))
            return key;
        if (kind == "planet")
            kind = "space-location";
        key = $"{kind}/{id}";
        if (_icons.Contains(key))
            return key;
        if (kind == "entity" && _icons.Contains($"item/{id}"))
            return $"item/{id}";
        return null;
    }

    public string IconFile(string key) => Path.Combine(Directory, "icons", key + ".png");

    // ------------------------------------------------------------------ loading

    public static GameData? Load(string directory, Action<string> log)
    {
        string file = Path.Combine(directory, DataFileName);
        if (!File.Exists(file))
            return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            if ((int?)root["format"] != FormatVersion)
            {
                log($"  The game data in {directory} was exported by another version of this tool.");
                return null;
            }
            var names = root["names"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!, StringComparer.Ordinal);
            var icons = root["icons"]!.AsArray().Select(n => (string)n!).ToHashSet(StringComparer.Ordinal);
            var entityIcons = root["entityIcons"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!, StringComparer.Ordinal);
            var sprites = (root["sprites"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value!.AsObject().ToDictionary(v => v.Key, v =>
            {
                var b = v.Value!.AsArray();
                return new SpriteBox((float)b[0]!, (float)b[1]!, (float)b[2]!, (float)b[3]!);
            }, StringComparer.Ordinal), StringComparer.Ordinal);
            var tiles = (root["tiles"] as JsonObject ?? []).ToDictionary(p => p.Key, p =>
            {
                var t = p.Value!.AsArray();
                return new TileTexture((int)t[0]! == 1, (int)t[1]!, (int)t[2]!, (int)t[3]!);
            }, StringComparer.Ordinal);
            var data = new GameData(Path.GetFullPath(directory), (string?)root["gameVersion"] ?? "", ReadChart(root["chart"]!.AsObject()), names, icons, entityIcons, sprites, tiles)
            {
                Space = root["space"]?.DeepClone() as JsonObject,
                _stages = (root["stages"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value!.AsArray().Select(v => (long)(double)v!).ToArray(), StringComparer.Ordinal),
            };
            log($"  Factorio {data.GameVersion}: {names.Count:N0} names, {icons.Count:N0} icons, sprites of {sprites.Count:N0} entities, {tiles.Count:N0} ground textures");
            return data;
        }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or InvalidOperationException or FormatException)
        {
            log($"  The game data in {directory} cannot be read ({ex.Message}).");
            return null;
        }
    }

    private static ChartColors ReadChart(JsonObject chart)
    {
        SKColor C(string name, SKColor fallback) => chart[name] is JsonArray a ? ToColor(a) : fallback;
        var defaults = new ChartColors();
        return new ChartColors
        {
            DefaultFriendly = C("default_friendly_color", defaults.DefaultFriendly),
            DefaultEnemy = C("default_enemy_color", defaults.DefaultEnemy),
            Rail = C("rail_color", defaults.Rail),
            ElevatedRail = C("elevated_rail_color", defaults.ElevatedRail),
            RailRamp = C("rail_ramp_color", defaults.RailRamp),
            ElectricPole = C("electric_power_pole_color", defaults.ElectricPole),
            CopperWire = C("copper_wire_color", defaults.CopperWire),
            FriendlyByType = (chart["default_friendly_color_by_type"] as JsonObject ?? []).ToDictionary(p => p.Key, p => ToColor(p.Value!)),
            ByType = (chart["default_color_by_type"] as JsonObject ?? []).ToDictionary(p => p.Key, p => ToColor(p.Value!)),
        };
    }

    /// <summary>A Factorio colour: [r, g, b, a] or {r, g, b, a}, with 0–1 or 0–255 components.</summary>
    public static SKColor ToColor(JsonNode node)
    {
        double[] v = node is JsonArray a
            ? a.Select(x => (double)x!).ToArray()
            : new[] { "r", "g", "b", "a" }.Select(k => node[k] is { } x ? (double)x : k == "a" ? 1 : 0).ToArray();
        bool unit = v.Take(3).All(x => x <= 1);
        byte B(double x) => (byte)Math.Clamp(Math.Round(unit ? x * 255 : x), 0, 255);
        return new SKColor(B(v[0]), B(v[1]), B(v[2]), v.Length > 3 ? B(v[3]) : (byte)255);
    }

    // ------------------------------------------------------------------ export from an installation

    /// <summary>Prototype types whose icons the map uses, and the kind they are filed under.</summary>
    private static readonly Dictionary<string, string> IconKinds = new(StringComparer.Ordinal)
    {
        ["fluid"] = "fluid", ["virtual-signal"] = "virtual-signal", ["space-location"] = "space-location", ["planet"] = "space-location",
        ["quality"] = "quality", ["recipe"] = "recipe", ["asteroid-chunk"] = "asteroid-chunk",
    };

    /// <summary>Not drawn on the map, never clicked: no icon needed.</summary>
    private static readonly HashSet<string> SkippedTypes =
    [
        "technology", "tile", "achievement", "tips-and-tricks-item", "shortcut", "item-group", "item-subgroup", "equipment-grid",
        "explosion", "optimized-particle", "smoke-with-trigger", "trivial-smoke", "projectile", "artillery-projectile", "beam", "stream",
        "corpse", "rail-remnants", "fire", "sticker", "particle-source", "speech-bubble", "highlight-box", "arrow", "artillery-flare",
        "entity-ghost", "tile-ghost", "item-request-proxy", "deconstructible-tile-proxy", "noise-expression", "noise-function", "custom-input",
    ];

    /// <summary>
    /// Writes names, icons and chart colours of the game in <paramref name="game"/> into <paramref name="target"/>. Needs
    /// the full game (the headless server has no graphics) and runs it once for its prototypes (--dump-data).
    /// </summary>
    public static void Export(FactorioInstallation game, string binary, string workDirectory, string target, Action<string> log)
    {
        if (!game.HasGraphics)
            throw new InvalidOperationException($"The Factorio in {game.Directory} has no graphics (a headless server?); icons need the full game.");
        log($"Exporting the game data of Factorio {game.VersionText} into {Path.GetFullPath(target)}");
        string dumpFile = FactorioRunner.DumpData(game, binary, workDirectory, log);
        var dump = JsonNode.Parse(File.ReadAllText(dumpFile))!.AsObject();

        var locale = ReadLocale(game.DataDirectory);
        var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var icons = new SortedSet<string>(StringComparer.Ordinal);
        var entityIcons = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string iconDirectory = Path.Combine(target, "icons");
        if (System.IO.Directory.Exists(iconDirectory))
            System.IO.Directory.Delete(iconDirectory, recursive: true);
        var cache = new Dictionary<string, SKBitmap?>(StringComparer.Ordinal);

        // Which item places which entity: the entity takes the item's icon when it has none of its own.
        var placedBy = new Dictionary<string, string>(StringComparer.Ordinal);
        int written = 0;
        foreach (var (type, entries) in dump)
        {
            if (SkippedTypes.Contains(type) || entries is not JsonObject prototypes)
                continue;
            foreach (var (id, node) in prototypes)
            {
                if (node is not JsonObject prototype)
                    continue;
                bool item = prototype.ContainsKey("stack_size");
                bool entity = !item && !IconKinds.ContainsKey(type) && (prototype.ContainsKey("collision_box") || prototype.ContainsKey("selection_box") || prototype.ContainsKey("max_health"));
                string? kind = item ? "item" : IconKinds.GetValueOrDefault(type) ?? (entity ? "entity" : null);
                if (kind is null)
                    continue;
                string key = $"{kind}/{id}";
                if (item && prototype["place_result"] is JsonValue place && (string?)place is { Length: > 0 } placed)
                    placedBy.TryAdd(placed, key);

                string? name = LocalisedName(prototype["localised_name"], locale) ?? Lookup(locale, $"{(kind == "space-location" ? "space-location" : kind)}-name.{id}");
                if (name is not null)
                    names[key] = name;

                if (icons.Contains(key) || !HasIcon(prototype))
                    continue;
                using var bitmap = RenderIcon(prototype, game.DataDirectory, cache);
                if (bitmap is null)
                    continue;
                string file = Path.Combine(iconDirectory, kind, id + ".png");
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using (var stream = File.Create(file))
                    bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
                icons.Add(key);
                written++;
            }
        }
        foreach (var (entity, item) in placedBy)
            if (!icons.Contains($"entity/{entity}") && icons.Contains(item))
                entityIcons[entity] = item;
        foreach (var bitmap in cache.Values)
            bitmap?.Dispose();

        string spriteDirectory = Path.Combine(target, "sprites");
        if (System.IO.Directory.Exists(spriteDirectory))
            System.IO.Directory.Delete(spriteDirectory, recursive: true);
        var sprites = SpriteExport.Export(dump, game.DataDirectory, spriteDirectory, log);
        string tileDirectory = Path.Combine(target, "tiles");
        if (System.IO.Directory.Exists(tileDirectory))
            System.IO.Directory.Delete(tileDirectory, recursive: true);
        var tiles = SpriteExport.ExportTiles(dump, game.DataDirectory, tileDirectory, log);

        var space = ExportSpace(dump, game.DataDirectory, Path.Combine(target, "space"), locale, log);

        string logo = Path.Combine(game.DataDirectory, "core", "graphics", "factorio-icon.png");
        if (File.Exists(logo))
            File.Copy(logo, Path.Combine(target, "logo.png"), overwrite: true);

        var chart = dump["utility-constants"]?["default"]?["chart"]?.AsObject() ?? [];
        var keep = new[] { "default_friendly_color", "default_enemy_color", "rail_color", "elevated_rail_color", "rail_ramp_color",
            "electric_power_pole_color", "copper_wire_color", "default_friendly_color_by_type", "default_color_by_type" };
        var data = new JsonObject
        {
            ["format"] = FormatVersion,
            ["gameVersion"] = game.VersionText,
            ["chart"] = new JsonObject(keep.Where(chart.ContainsKey).Select(k => KeyValuePair.Create(k, (JsonNode?)chart[k]!.DeepClone()))),
            ["names"] = new JsonObject(names.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
            ["icons"] = new JsonArray(icons.Select(i => (JsonNode?)i).ToArray()),
            ["entityIcons"] = new JsonObject(entityIcons.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
            ["sprites"] = new JsonObject(sprites.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)new JsonObject(p.Value.Select(v =>
                KeyValuePair.Create(v.Key, (JsonNode?)new JsonArray(Math.Round(v.Value.Left, 3), Math.Round(v.Value.Top, 3), Math.Round(v.Value.Width, 3), Math.Round(v.Value.Height, 3)))))))),
            ["space"] = space,
            ["stages"] = new JsonObject((dump["resource"] as JsonObject ?? []).Where(r => r.Value?["stage_counts"] is JsonArray)
                .Select(r => KeyValuePair.Create(r.Key, (JsonNode?)r.Value!["stage_counts"]!.DeepClone()))),
            ["tiles"] = new JsonObject(tiles.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)new JsonArray(p.Value.Material ? 1 : 0, p.Value.Count, p.Value.Width, p.Value.Height)))),
        };
        System.IO.Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, DataFileName), data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        log($"  {names.Count:N0} names, {written:N0} icons");
    }

    /// <summary>
    /// The star map: every planet and space location with its place (distance from the sun and direction, as the game
    /// gives them), size and picture, the routes between them, the sun and the pictures of platforms.
    /// </summary>
    private static JsonObject ExportSpace(JsonObject dump, string dataDirectory, string directory, Dictionary<string, string> locale, Action<string> log)
    {
        if (System.IO.Directory.Exists(directory))
            System.IO.Directory.Delete(directory, recursive: true);
        var locations = new JsonArray();
        foreach (string type in new[] { "planet", "space-location" })
            foreach (var (name, node) in dump[type] as JsonObject ?? [])
            {
                if (node is not JsonObject p || (string?)p["starmap_icon"] is not { } picture)
                    continue;
                // a globe from the texture the game wraps around the planet, where there is one; else the star map's picture
                string target = Path.Combine(directory, name + ".png");
                bool globe = System.IO.Directory.EnumerateDirectories(dataDirectory)
                    .Select(mod => Path.Combine(mod, "graphics", "space", name + ".png"))
                    .FirstOrDefault(File.Exists) is { } texture && PlanetRenderer.Render(texture, target);
                if (!globe && !SpriteExport.CopyPicture(picture, dataDirectory, target))
                    continue;
                locations.Add(new JsonObject
                {
                    ["name"] = name, ["planet"] = type == "planet",
                    // the globe's picture has room for its atmosphere around it
                    ["scale"] = globe ? (double)PlanetRenderer.Size / PlanetRenderer.Diameter : 1,
                    ["title"] = Lookup(locale, $"space-location-name.{name}") ?? Readable(name),
                    ["distance"] = (double?)p["distance"] ?? 0, ["orientation"] = (double?)p["orientation"] ?? 0, ["magnitude"] = (double?)p["magnitude"] ?? 1,
                });
            }
        var connections = new JsonArray();
        foreach (var (name, node) in dump["space-connection"] as JsonObject ?? [])
            if (node is JsonObject c)
                connections.Add(new JsonObject { ["name"] = name, ["from"] = (string?)c["from"], ["to"] = (string?)c["to"], ["length"] = (double?)c["length"] ?? 0 });
        var sprites = dump["utility-sprites"]?["default"];
        foreach (string sprite in new[] { "starmap_star", "starmap_platform_stopped", "starmap_platform_moving" })
            SpriteExport.ExportPicture(sprites?[sprite], dataDirectory, Path.Combine(directory, sprite + ".png"));
        log($"  star map with {locations.Count} places and {connections.Count} routes");
        return new JsonObject { ["locations"] = locations, ["connections"] = connections };
    }

    private static bool HasIcon(JsonObject prototype) => prototype.ContainsKey("icon") || prototype["icons"] is JsonArray { Count: > 0 };

    // ------------------------------------------------------------------ locale

    /// <summary>The English names: "section.key" → text, from every mod's locale/en/*.cfg.</summary>
    internal static Dictionary<string, string> ReadLocale(string dataDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in System.IO.Directory.EnumerateFiles(dataDirectory, "*.cfg", SearchOption.AllDirectories)
                     .Where(f => f.Replace('\\', '/').Contains("/locale/en/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            string section = "";
            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] is ';' or '#')
                    continue;
                if (line[0] == '[' && line[^1] == ']')
                {
                    section = line[1..^1];
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0)
                    result[section.Length > 0 ? $"{section}.{line[..eq]}" : line[..eq]] = line[(eq + 1)..];
            }
        }
        return result;
    }

    private static readonly Regex Reference = new(@"__(ITEM|ENTITY|FLUID|TILE|SPACE_LOCATION|RECIPE|TECHNOLOGY)__([^_]+(?:_[^_]+)*)__", RegexOptions.Compiled);

    private static string? Lookup(Dictionary<string, string> locale, string key)
    {
        if (!locale.TryGetValue(key, out string? text))
            return null;
        // "__ITEM__iron-plate__ barrel" → "Iron plate barrel"
        text = Reference.Replace(text, m =>
        {
            string section = m.Groups[1].Value.ToLowerInvariant().Replace('_', '-') + "-name";
            return locale.GetValueOrDefault($"{section}.{m.Groups[2].Value}") ?? Readable(m.Groups[2].Value);
        });
        return text.Contains("__", StringComparison.Ordinal) ? null : text;
    }

    /// <summary>The simple forms of a localised name: ["item-name.x"] or "item-name.x".</summary>
    private static string? LocalisedName(JsonNode? node, Dictionary<string, string> locale) => node switch
    {
        JsonArray { Count: 1 } a when a[0] is JsonValue v && (string?)v is { } key => Lookup(locale, key),
        JsonValue v when v.TryGetValue(out string? key) && key.Contains('.') => Lookup(locale, key),
        _ => null,
    };

    // ------------------------------------------------------------------ icons

    private const int IconSize = 64;

    /// <summary>
    /// The icon at 64 px. An "icon" file holds the icon and its smaller mipmaps side by side; only the first square
    /// is taken. "icons" are layers, each with its own size, scale, shift and tint, drawn over each other the
    /// way the game does: the default scale fills the icon, a scale of 0.5 half of it.
    /// </summary>
    private static SKBitmap? RenderIcon(JsonObject prototype, string dataDirectory, Dictionary<string, SKBitmap?> cache)
    {
        var layers = prototype["icons"] is JsonArray array
            ? array.OfType<JsonObject>().ToList()
            : [new JsonObject { ["icon"] = prototype["icon"]?.DeepClone(), ["icon_size"] = prototype["icon_size"]?.DeepClone() }];
        var result = new SKBitmap(IconSize, IconSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        bool drawn = false;
        foreach (var layer in layers)
        {
            if ((string?)layer["icon"] is not { } path || Load(path, dataDirectory, cache) is not { } image)
                continue;
            int size = layer["icon_size"] is { } s ? (int)(double)s : prototype["icon_size"] is { } ps ? (int)(double)ps : 64;
            size = Math.Min(size, Math.Min(image.Width, image.Height));
            // in the game's units: 32 is the whole icon
            double scale = layer["scale"] is { } sc ? (double)sc : 32.0 / size;
            double width = size * scale * IconSize / 32.0;
            double sx = 0, sy = 0;
            if (layer["shift"] is JsonArray shift && shift.Count >= 2)
                (sx, sy) = ((double)shift[0]! * IconSize / 32.0, (double)shift[1]! * IconSize / 32.0);
            else if (layer["shift"] is JsonObject shiftObject)
                (sx, sy) = ((double)(shiftObject["x"] ?? 0) * IconSize / 32.0, (double)(shiftObject["y"] ?? 0) * IconSize / 32.0);
            var dest = SKRect.Create((float)(IconSize / 2.0 + sx - width / 2), (float)(IconSize / 2.0 + sy - width / 2), (float)width, (float)width);
            using var paint = new SKPaint { IsAntialias = true };
            if (layer["tint"] is { } tintNode)
            {
                var tint = ToColor(tintNode);
                paint.ColorFilter = SKColorFilter.CreateBlendMode(tint.WithAlpha(255), SKBlendMode.Modulate);
                paint.Color = SKColors.White.WithAlpha(tint.Alpha);
            }
            canvas.DrawImage(SKImage.FromBitmap(image), SKRect.Create(0, 0, size, size), dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            drawn = true;
        }
        if (drawn)
            return result;
        result.Dispose();
        return null;
    }

    /// <summary>"__base__/graphics/icons/x.png" → data/base/graphics/icons/x.png</summary>
    private static SKBitmap? Load(string path, string dataDirectory, Dictionary<string, SKBitmap?> cache)
    {
        if (cache.TryGetValue(path, out var cached))
            return cached;
        var m = Regex.Match(path, "^__([^_/]+(?:[_-][^_/]+)*)__/(.+)$");
        string file = m.Success ? Path.Combine(dataDirectory, m.Groups[1].Value, m.Groups[2].Value) : Path.Combine(dataDirectory, path);
        var bitmap = File.Exists(file) ? SKBitmap.Decode(file) : null;
        cache[path] = bitmap;
        return bitmap;
    }
}
