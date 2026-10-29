using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using FactorioMapRenderer.Game;
using FactorioMapRenderer.Save;
using FactorioMapRenderer.World;

namespace FactorioMapRenderer.Rendering;

/// <summary>
/// Writes the Leaflet viewer (index.html and its assets), map-data.js with what all surfaces share, and one
/// surfaces/N.js per surface that the viewer loads when the surface is shown.
/// </summary>
public static partial class HtmlExporter
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Export(MapExport export, IReadOnlyList<SurfaceMap> surfaces, IReadOnlyDictionary<int, Dictionary<string, TileSet>> tiles,
        string outputDirectory, int maxZoom, GameData game)
    {
        CopyAssets(outputDirectory);
        string? logo = CopyLogo(outputDirectory, game);
        var icons = new IconTable(game, outputDirectory);

        string surfaceDirectory = Path.Combine(outputDirectory, "surfaces");
        if (Directory.Exists(surfaceDirectory))
            Directory.Delete(surfaceDirectory, recursive: true);
        Directory.CreateDirectory(surfaceDirectory);
        var closeUp = new CloseUpFiles(game, outputDirectory);
        foreach (var surface in surfaces)
        {
            using var stream = File.Create(Path.Combine(surfaceDirectory, $"{surface.Data.Index}.js"));
            stream.Write("(window.MAP_SURFACES = window.MAP_SURFACES || {})["u8);
            stream.Write(System.Text.Encoding.ASCII.GetBytes(surface.Data.Index.ToString(CultureInfo.InvariantCulture)));
            stream.Write("] = "u8);
            using (var w = new Utf8JsonWriter(stream, WriterOptions))
                WriteSurface(w, surface, tiles.GetValueOrDefault(surface.Data.Index) ?? [], icons, closeUp, surfaceDirectory);
            stream.Write(";\n"u8);
        }

        using (var stream = File.Create(Path.Combine(outputDirectory, "map-data.js")))
        {
            stream.Write("window.MAP_DATA = "u8);
            using (var w = new Utf8JsonWriter(stream, WriterOptions))
                WriteData(w, export, surfaces, maxZoom, icons, logo, game, outputDirectory);
            stream.Write(";\n"u8);
        }
    }

    /// <summary>The viewer without any save, for a served map that waits for one to be chosen.</summary>
    public static void ExportEmpty(string outputDirectory, int maxZoom, GameData game)
    {
        Directory.CreateDirectory(outputDirectory);
        CopyAssets(outputDirectory);
        string? logo = CopyLogo(outputDirectory, game);
        using var stream = File.Create(Path.Combine(outputDirectory, "map-data.js"));
        stream.Write("window.MAP_DATA = "u8);
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("title", "Factorio Map");
            w.WriteBoolean("empty", true);
            w.WriteString("fileName", "");
            w.WriteNumber("maxZoom", maxZoom);
            if (logo is not null)
                w.WriteString("logo", logo);
            foreach (string name in new[] { "summary", "icons", "layers", "surfaces" })
            {
                w.WriteStartArray(name);
                w.WriteEndArray();
            }
            w.WriteStartObject("rich");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        stream.Write(";\n"u8);
    }

    /// <summary>Brings the viewer files of an existing output up to date without touching its map.</summary>
    public static void RefreshViewer(string outputDirectory, GameData game)
    {
        CopyAssets(outputDirectory);
        if (File.Exists(Path.Combine(outputDirectory, GameLogoFile)))
            CopyLogo(outputDirectory, game);
    }

    private const string GameLogoFile = "game-logo.png";

    private static string? CopyLogo(string outputDirectory, GameData game)
    {
        if (game.LogoFile is not { } logo)
            return null;
        File.Copy(logo, Path.Combine(outputDirectory, GameLogoFile), overwrite: true);
        return GameLogoFile;
    }

    /// <summary>Icons used by the map, copied next to it; each gets an index into MAP_DATA.icons.</summary>
    private sealed class IconTable(GameData game, string outputDirectory)
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        public List<string> Urls { get; } = [];
        private bool _cleared;

        public int this[string? key]
        {
            get
            {
                if (key is null)
                    return -1;
                if (_index.TryGetValue(key, out int index))
                    return index;
                string source = game.IconFile(key);
                if (!File.Exists(source))
                    return _index[key] = -1;
                string directory = Path.Combine(outputDirectory, "icons");
                if (!_cleared)
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                    Directory.CreateDirectory(directory);
                    _cleared = true;
                }
                string url = "icons/" + key.Replace('/', '-') + ".png";
                File.Copy(source, Path.Combine(outputDirectory, url), overwrite: true);
                Urls.Add(url);
                return _index[key] = Urls.Count - 1;
            }
        }
    }

    private static void WriteData(Utf8JsonWriter w, MapExport export, IReadOnlyList<SurfaceMap> surfaces, int maxZoom, IconTable icons, string? logo, GameData game,
        string outputDirectory)
    {
        w.WriteStartObject();
        string title = export.Header.Name;
        w.WriteString("title", title);
        w.WriteString("fileName", export.FileName);
        if (export.SavedAt is { } saved)
            w.WriteString("savedAt", saved.ToString("o", CultureInfo.InvariantCulture));
        w.WriteString("renderedBy", Version);
        // Names the files of this render: the viewer asks for them with ?v=<build>, so that the browser can keep
        // them for good and a new render, which reuses the names, still reaches it.
        w.WriteString("build", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
        w.WriteString("gameVersion", export.Header.GameVersionText);
        if (export.Header.Untested)
            w.WriteString("testedUpTo", SaveHeader.TestedUpTo.ToString(3));
        w.WriteNumber("maxZoom", maxZoom);
        w.WriteNumber("pixelsPerTileAtZoom0", TileRenderer.PixelsPerTileAtZoom0);
        if (logo is not null)
            w.WriteString("logo", logo);

        w.WriteStartArray("layers");
        foreach (var layer in Layers.All)
        {
            w.WriteStartObject();
            w.WriteString("id", layer.Id);
            w.WriteString("title", layer.Title);
            w.WriteString("group", layer.Group);
            w.WriteString("kind", layer.Kind == LayerKind.Raster ? "raster" : "marker");
            w.WriteBoolean("visible", layer.VisibleByDefault);
            w.WriteString("color", layer.Color);
            w.WriteString("description", layer.Description);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("surfaces");
        foreach (var s in surfaces)
        {
            w.WriteStartObject();
            w.WriteNumber("id", s.Data.Index);
            w.WriteString("name", s.Data.Name);
            w.WriteString("title", s.Title);
            w.WriteString("kind", s.Kind);
            if (s.Data.Location is { } location)
                w.WriteString("location", game.Name("space-location", location));
            w.WriteNumber("icon", icons[s.Icon]);
            w.WriteNumber("chunks", s.Data.Chunks.Count);
            w.WriteNumber("entities", s.Data.Entities.Count);
            w.WriteNumber("minZoom", TileRenderer.MinZoom(s, maxZoom));
            w.WriteStartArray("bounds");
            foreach (int v in new[] { s.Bounds.MinX, s.Bounds.MinY, s.Bounds.MaxX, s.Bounds.MaxY })
                w.WriteNumberValue(v);
            w.WriteEndArray();
            // where the factory is: the view opens there instead of on the whole charted area
            if (Focus(s) is { } focus)
            {
                w.WriteStartArray("focus");
                foreach (double v in focus)
                    w.WriteNumberValue(Math.Round(v));
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("summary");
        foreach (var (key, value) in Summary(export, surfaces, game))
        {
            w.WriteStartArray();
            w.WriteStringValue(key);
            w.WriteStringValue(value);
            w.WriteEndArray();
        }
        w.WriteEndArray();

        WriteSpace(w, surfaces, icons, game, outputDirectory);

        // icons for rich text in names: "[item=iron-plate]", "[virtual-signal=signal-heart]", "[planet=nauvis]"
        w.WriteStartObject("rich");
        var names = surfaces.Select(s => s.Title)
            .Concat(surfaces.SelectMany(s => s.Markers.Values.SelectMany(m => m.Markers.Select(x => x.Name))));
        foreach (string tag in names.SelectMany(n => RichText.Matches(n).Select(m => m.Value)).Distinct(StringComparer.Ordinal))
        {
            var m = RichText.Match(tag);
            string kind = m.Groups[1].Value switch { "virtual-signal" => "virtual-signal", "planet" or "space-location" => "space-location", var k => k };
            string id = m.Groups[2].Value.Split(',')[0];
            int index = icons[game.IconKey(kind, id)];
            if (index >= 0)
                w.WriteNumber($"{m.Groups[1].Value}={m.Groups[2].Value}", index);
        }
        w.WriteEndObject();

        w.WriteStartArray("icons");
        foreach (string url in icons.Urls)
            w.WriteStringValue(url);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>
    /// The solar system for the star map view: places with their pictures (copied next to the map), routes, and
    /// where each platform is. Planets that have a surface in this save can be opened from there.
    /// </summary>
    private static void WriteSpace(Utf8JsonWriter w, IReadOnlyList<SurfaceMap> surfaces, IconTable icons, GameData game, string outputDirectory)
    {
        if (game.Space is not { } space)
            return;
        string directory = Path.Combine(outputDirectory, "space");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        string? Copy(string name)
        {
            string source = game.SpaceFile(name);
            if (!File.Exists(source))
                return null;
            File.Copy(source, Path.Combine(directory, name + ".png"), overwrite: true);
            return $"space/{name}.png";
        }

        w.WriteStartObject("space");
        w.WriteStartArray("locations");
        foreach (var location in space["locations"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
        {
            string name = (string)location["name"]!;
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("title", game.Name("space-location", name));
            w.WriteBoolean("planet", (bool?)location["planet"] ?? false);
            w.WriteNumber("distance", (double)location["distance"]!);
            w.WriteNumber("orientation", (double)location["orientation"]!);
            w.WriteNumber("magnitude", (double)location["magnitude"]!);
            w.WriteNumber("scale", (double?)location["scale"] ?? 1);
            if (Copy(name) is { } image)
                w.WriteString("image", image);
            if (surfaces.FirstOrDefault(s => s.Data.Planet == name) is { } surface)
                w.WriteNumber("surface", surface.Data.Index);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("connections");
        foreach (var connection in space["connections"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
        {
            w.WriteStartObject();
            w.WriteString("name", (string?)connection["name"]);
            w.WriteString("from", (string?)connection["from"]);
            w.WriteString("to", (string?)connection["to"]);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("platforms");
        foreach (var s in surfaces.Where(s => s.Kind == "platform"))
        {
            w.WriteStartObject();
            w.WriteNumber("surface", s.Data.Index);
            w.WriteString("title", s.Title);
            if (s.Data.Location is { } location)
                w.WriteString("location", location);
            if (s.Data.Connection is { } connection)
            {
                w.WriteString("connection", connection);
                w.WriteNumber("distance", Math.Round(s.Data.Distance, 4));
            }
            w.WriteNumber("entities", s.Data.Entities.Count);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        foreach (string sprite in new[] { "starmap_star", "starmap_platform_stopped", "starmap_platform_moving" })
            if (Copy(sprite) is { } image)
                w.WriteString(sprite, image);
        w.WriteEndObject();
    }

    private static readonly Regex RichText = new(@"\[(item|fluid|entity|virtual-signal|planet|space-location|recipe|technology|quality)=([^\]]+)\]", RegexOptions.Compiled);

    private static IEnumerable<(string, string)> Summary(MapExport export, IReadOnlyList<SurfaceMap> surfaces, GameData game)
    {
        yield return ("Save", export.Header.Name);
        yield return ("Saved by Factorio", export.Header.Untested
            ? $"{export.Header.GameVersionText} (untested: newer than {SaveHeader.TestedUpTo.ToString(3)})"
            : export.Header.GameVersionText);
        yield return ("Scenario", GameData.Readable(export.Header.Scenario));
        if (export.TicksPlayed > 0)
            yield return ("Play time", SurfaceMap.Duration(export.TicksPlayed));
        var mods = export.Mods.Keys.Where(m => m is not "base" and not "core").Order(StringComparer.Ordinal).ToList();
        yield return ("Mods", mods.Count == 0 ? "None" : string.Join(", ", mods.Select(GameData.Readable)));
        if (export.PlayerForce is { } force)
        {
            if (force.Technologies > 0)
                yield return ("Research", $"{force.Researched} of {force.Technologies} technologies");
            if (force.Research is { } research)
                yield return ("Researching", $"{game.Name("technology", research)} ({force.ResearchProgress:P0})");
            if (force.RocketsLaunched > 0)
                yield return ("Rockets launched", force.RocketsLaunched.ToString("N0", CultureInfo.InvariantCulture));
        }
        if (export.Players.Count > 0)
            yield return ("Players", $"{export.Players.Count} ({string.Join(", ", export.Players.Select(p => p.Name))})");
        int planets = surfaces.Count(s => s.Kind == "planet"), platforms = surfaces.Count(s => s.Kind == "platform");
        yield return ("Surfaces", string.Join(", ", new[]
        {
            planets > 0 ? $"{planets} planet{(planets == 1 ? "" : "s")}" : null,
            platforms > 0 ? $"{platforms} space platform{(platforms == 1 ? "" : "s")}" : null,
            surfaces.Count - planets - platforms is var other and > 0 ? $"{other} other" : null,
        }.OfType<string>()));
        // from the save itself: also when nothing of Nauvis is on the map
        if ((export.Surfaces.FirstOrDefault(s => s.Planet == "nauvis") ?? export.Surfaces.FirstOrDefault())?.Seed is { } seed)
            yield return ("Map seed", seed.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The area with most of the buildings, so that a large charted map opens on the factory.</summary>
    private static double[]? Focus(SurfaceMap s)
    {
        var buildings = s.Data.Entities.Where(e => s.Data.Prototypes[e.Prototype].Type is not ("unit-spawner" or "turret" or "segmented-unit" or "spider-unit")).ToList();
        if (buildings.Count < 10)
            return null;
        var xs = buildings.Select(e => (double)e.X).Order().ToList();
        var ys = buildings.Select(e => (double)e.Y).Order().ToList();
        // the middle 96 %: a lonely outpost far away does not zoom the view out to everything
        int lo = buildings.Count / 50, hi = buildings.Count - 1 - buildings.Count / 50;
        return [xs[lo] - 32, ys[lo] - 32, xs[hi] + 32, ys[hi] + 32];
    }

    private static void WriteSurface(Utf8JsonWriter w, SurfaceMap s, Dictionary<string, TileSet> tiles, IconTable icons, CloseUpFiles closeUp,
        string surfaceDirectory)
    {
        w.WriteStartObject();
        if (closeUp.Game.HasGraphics)
            WriteCloseUp(w, s, icons, closeUp, Path.Combine(surfaceDirectory, s.Data.Index.ToString(CultureInfo.InvariantCulture)));
        w.WriteStartObject("tiles");
        foreach (var (layer, set) in tiles)
        {
            w.WriteStartObject(layer);
            w.WriteNumber("minNativeZoom", set.MinNativeZoom);
            w.WriteNumber("maxNativeZoom", set.MaxNativeZoom);
            // one string instead of an array of them: half the size, and quick to split
            w.WriteString("list", string.Join(' ', set.Tiles));
            w.WriteEndObject();
        }
        w.WriteEndObject();

        w.WriteStartObject("markers");
        foreach (var (layer, markers) in s.Markers)
        {
            w.WriteStartObject(layer.Id);
            w.WriteStartArray("legend");
            for (int i = 0; i < markers.Legend.Count; i++)
            {
                var (label, color, icon) = markers.Legend[i];
                w.WriteStartArray();
                w.WriteStringValue(label);
                w.WriteStringValue(color);
                w.WriteNumberValue(markers.Markers.Count(m => m.Legend == i));
                w.WriteNumberValue(icons[icon]);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteStartArray("items");
            foreach (var m in markers.Markers)
            {
                // [x, y, name, color, legend, dimmed, details, icon, ring]
                w.WriteStartArray();
                w.WriteNumberValue(Math.Round(m.X, 2));
                w.WriteNumberValue(Math.Round(m.Y, 2));
                w.WriteStringValue(m.Name);
                w.WriteStringValue(m.Color);
                w.WriteNumberValue(m.Legend);
                w.WriteNumberValue(m.Dimmed ? 1 : 0);
                WriteRows(w, m.Details);
                w.WriteNumberValue(icons[m.Icon]);
                w.WriteStringValue(m.Ring ?? "");
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndObject();

        w.WriteStartArray("inspect");
        foreach (var item in s.Inspect)
        {
            // [x, y, radius, name, group, details, icon]
            w.WriteStartArray();
            w.WriteNumberValue(Math.Round(item.X, 2));
            w.WriteNumberValue(Math.Round(item.Y, 2));
            w.WriteNumberValue(Math.Round(item.Radius, 2));
            w.WriteStringValue(item.Name);
            w.WriteStringValue(item.Group);
            WriteRows(w, item.Details);
            w.WriteNumberValue(icons[item.Icon]);
            w.WriteEndArray();
        }
        w.WriteEndArray();

        // the alt mode: [x, y, size, [icon…], [module icon…]]
        w.WriteStartArray("alt");
        foreach (var alt in s.Alt)
        {
            w.WriteStartArray();
            w.WriteNumberValue(Math.Round(alt.X, 2));
            w.WriteNumberValue(Math.Round(alt.Y, 2));
            w.WriteNumberValue(Math.Round(alt.Size, 2));
            foreach (var list in new[] { alt.Icons, alt.Modules })
            {
                w.WriteStartArray();
                foreach (string icon in list)
                    w.WriteNumberValue(icons[icon]);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        w.WriteEndArray();

        foreach (var (name, list) in new[] { ("counts", s.Counts.Select(c => (c.Name, (double)c.Count, c.Icon))), ("production", s.Production.Select(p => (p.Name, p.Count, p.Icon))) })
        {
            w.WriteStartArray(name);
            foreach (var (label, count, icon) in list)
            {
                w.WriteStartArray();
                w.WriteStringValue(label);
                w.WriteNumberValue(Math.Round(count));
                w.WriteNumberValue(icons[icon]);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteRows(Utf8JsonWriter w, IReadOnlyList<string[]> rows)
    {
        w.WriteStartArray();
        foreach (var row in rows)
        {
            w.WriteStartArray();
            foreach (string cell in row)
                w.WriteStringValue(cell);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    private static void CopyAssets(string outputDirectory)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            string name = resource.Replace('\\', '/');
            // the export mod is for the game, not the page
            if (!name.StartsWith("Assets/", StringComparison.Ordinal) || name.StartsWith("Assets/exporter/", StringComparison.Ordinal))
                continue;
            // Assets/viewer/* goes to the root, Assets/leaflet/* to leaflet/.
            string relative = name["Assets/".Length..];
            if (relative.StartsWith("viewer/", StringComparison.Ordinal))
                relative = relative["viewer/".Length..];
            string target = Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = assembly.GetManifestResourceStream(resource)!;
            using var file = File.Create(target);
            source.CopyTo(file);
        }

        // Which renderer wrote the viewer, for the footer and the update check against the project's releases.
        // After an update the server notes which version the map came from ("" if that was not recorded yet).
        string noted = Path.Combine(outputDirectory, UpdatedFromFile);
        string? updatedFrom = File.Exists(noted) ? File.ReadAllText(noted).Trim() : null;
        File.WriteAllText(Path.Combine(outputDirectory, "version.js"),
            $"window.MAP_RENDERER = {JsonSerializer.Serialize(new { version = Version, repository = Repository, updatedFrom, changes = Changes() })};\n");
    }

    /// <summary>
    /// The changelog for the "what is new" window of the viewer: the newest versions first, each with its
    /// sections (a title, possibly empty, and the entries under it).
    /// </summary>
    internal static List<object> Changes()
    {
        var versions = new List<(string Version, List<(string Title, List<string> Items)> Sections)>();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Changelog.md");
        if (stream is null)
            return [];
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                // "## 0.1.0 – 2026-10-27"
                versions.Add((line[3..].Split(' ')[0], []));
                continue;
            }
            if (versions.Count == 0 || line.Trim().Length == 0 || line.StartsWith("```", StringComparison.Ordinal))
                continue;
            var sections = versions[^1].Sections;
            if (line.StartsWith("### ", StringComparison.Ordinal))
                sections.Add((line[4..].Trim(), []));
            else if (line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (sections.Count == 0)
                    sections.Add(("", []));
                sections[^1].Items.Add(line[2..].Trim());
            }
            else if (sections.Count > 0 && sections[^1].Items.Count > 0)
                sections[^1].Items[^1] += " " + line.Trim(); // an entry that runs on over several lines
        }
        return [.. versions.Where(v => char.IsDigit(v.Version.FirstOrDefault())).Take(MaxChangelogVersions).Select(v => (object)new
        {
            version = v.Version,
            sections = v.Sections.Where(s => s.Items.Count > 0).Select(s => new { title = s.Title, items = s.Items }),
        })];
    }

    /// <summary>Written by the server into a map's folder when it finds a map of another version.</summary>
    public const string UpdatedFromFile = ".updated-from";

    /// <summary>The viewer only ever shows what is new since the last visit; older history is on GitHub.</summary>
    private const int MaxChangelogVersions = 8;

    /// <summary>GitHub repository whose releases the viewer checks for updates.</summary>
    public const string Repository = "Tunefish92/FactorioMapRenderer";

    /// <summary>The renderer's version without build metadata, e.g. "0.1.0" or "0.2.0-beta.1".</summary>
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}
