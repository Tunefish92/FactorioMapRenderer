using System.Globalization;
using System.Text.Json;
using FactorioMapRenderer.Save;
using SkiaSharp;

namespace FactorioMapRenderer.World;

public sealed record TileType(string Name, SKColor Color);

/// <param name="Width">Size in tiles, as the prototype gives it (rotated entities keep it).</param>
public sealed record EntityPrototype(string Name, string Type, SKColor? Map, SKColor? Friendly, SKColor? Enemy, int Width, int Height);

/// <summary>A building, vehicle, nest… with its bounding box in tiles; <see cref="Details"/> is the export's JSON.</summary>
public readonly record struct MapEntity(int Prototype, int Force, float X1, float Y1, float X2, float Y2, byte Direction, float X, float Y, string? Details);

/// <summary>A resource tile (with its amount), tree, rock or cliff.</summary>
/// <param name="Variant">A cliff's orientation ("west-to-east" …), which picks its picture.</param>
public readonly record struct MapPoint(int Prototype, float X, float Y, long Amount, string? Variant = null);

public readonly record struct Wire(float X1, float Y1, float X2, float Y2);

public sealed record ChartTag(string Text, double X, double Y, string? IconType, string? IconName, string? User);

/// <summary>One surface of the save: a planet or a space platform, as far as the player force has charted it.</summary>
public sealed class SurfaceData
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string? Planet { get; set; }
    public string? Platform { get; set; }
    /// <summary>Where a platform is: the planet it orbits.</summary>
    public string? Location { get; set; }
    /// <summary>A platform on its way: the route (space connection) and how far along it, 0 to 1.</summary>
    public string? Connection { get; set; }
    public double Distance { get; set; }
    public long? Seed { get; set; }

    public List<TileType> Tiles { get; } = [];
    public List<EntityPrototype> Prototypes { get; } = [];
    /// <summary>Charted chunks: 32 × 32 indexes into <see cref="Tiles"/>, row by row.</summary>
    public Dictionary<(int X, int Y), ushort[]> Chunks { get; } = [];
    public List<MapEntity> Entities { get; } = [];
    public List<MapPoint> Resources { get; } = [];
    public List<MapPoint> Nature { get; } = [];
    public List<Wire> Wires { get; } = [];
    public List<ChartTag> Tags { get; } = [];
    /// <summary>Entities that were only counted (biters, items on the ground, corpses): name → count.</summary>
    public Dictionary<string, long> Counted { get; } = [];

    public Dictionary<string, double> ItemsProduced { get; } = [];
    public Dictionary<string, double> FluidsProduced { get; } = [];

    public bool IsPlatform => Platform is not null;
}

public sealed record PlayerInfo(string Name, bool Connected, bool Admin, int? Surface, double X, double Y, long OnlineTicks, long LastOnlineTick, SKColor? Color);

public sealed record ForceInfo(int Index, string Name, long RocketsLaunched, int Researched, int Technologies, string? Research, double ResearchProgress);

/// <summary>Everything the export wrote about a save.</summary>
public sealed class MapExport
{
    public required SaveHeader Header { get; init; }
    public required string FileName { get; init; }
    public DateTime? SavedAt { get; init; }
    public long Tick { get; init; }
    public long TicksPlayed { get; init; }
    public Dictionary<string, string> Mods { get; init; } = [];
    public List<SurfaceData> Surfaces { get; } = [];
    public List<PlayerInfo> Players { get; } = [];
    public List<ForceInfo> Forces { get; } = [];

    public ForceInfo? PlayerForce => Forces.FirstOrDefault(f => f.Name == "player");

    /// <summary>Reads the folder the export mod wrote (meta.json and surface-N.txt).</summary>
    public static MapExport Read(string directory, SaveHeader header, string fileName, DateTime? savedAt)
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "meta.json")));
        var root = meta.RootElement;
        var export = new MapExport
        {
            Header = header, FileName = fileName, SavedAt = savedAt,
            Tick = root.TryGetProperty("tick", out var tick) ? tick.GetInt64() : 0,
            TicksPlayed = root.TryGetProperty("ticks_played", out var played) ? played.GetInt64() : 0,
            Mods = root.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Object
                ? mods.EnumerateObject().Where(m => m.Name != Game.FactorioRunner.ExportMod).ToDictionary(m => m.Name, m => m.Value.GetString() ?? "")
                : [],
        };

        foreach (var p in Array(root, "players"))
        {
            export.Players.Add(new PlayerInfo(
                Str(p, "name") ?? "?", Bool(p, "connected"), Bool(p, "admin"),
                p.TryGetProperty("surface", out var s) ? s.GetInt32() : null,
                Num(p, "x"), Num(p, "y"), (long)Num(p, "online_time"), (long)Num(p, "last_online"),
                p.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() >= 3
                    ? new SKColor((byte)c[0].GetInt32(), (byte)c[1].GetInt32(), (byte)c[2].GetInt32()) : null));
        }
        foreach (var f in Array(root, "forces"))
            export.Forces.Add(new ForceInfo((int)Num(f, "index"), Str(f, "name") ?? "", (long)Num(f, "rockets_launched"),
                (int)Num(f, "researched"), (int)Num(f, "technologies"), Str(f, "research"), Num(f, "research_progress")));

        foreach (var s in Array(root, "surfaces"))
        {
            string file = Path.Combine(directory, Str(s, "file") ?? "");
            if (!File.Exists(file))
                continue;
            var surface = ReadSurface(file);
            if (s.TryGetProperty("statistics", out var stats))
            {
                Totals(stats, "item", surface.ItemsProduced);
                Totals(stats, "fluid", surface.FluidsProduced);
            }
            export.Surfaces.Add(surface);
        }
        return export;
    }

    private static void Totals(JsonElement statistics, string kind, Dictionary<string, double> into)
    {
        if (statistics.TryGetProperty(kind, out var k) && k.TryGetProperty("produced", out var produced) && produced.ValueKind == JsonValueKind.Object)
            foreach (var item in produced.EnumerateObject())
                if (item.Value.ValueKind == JsonValueKind.Number)
                    into[item.Name] = item.Value.GetDouble();
    }

    /// <summary>Reads one surface file; its line format is described in Assets/exporter/control.lua.</summary>
    internal static SurfaceData ReadSurface(string file)
    {
        SurfaceData? surface = null;
        var tileCodes = new Dictionary<int, int>();
        var protoCodes = new Dictionary<int, int>();
        foreach (string line in File.ReadLines(file))
        {
            if (line.Length < 2)
                continue;
            string[] f = line.Split('\t');
            switch (f[0])
            {
                case "S":
                {
                    using var doc = JsonDocument.Parse(f[2]);
                    var m = doc.RootElement;
                    surface = new SurfaceData
                    {
                        Index = int.Parse(f[1], CultureInfo.InvariantCulture), Name = Str(m, "name") ?? f[1],
                        Planet = Str(m, "planet"), Platform = Str(m, "platform"), Location = Str(m, "location"),
                        Connection = Str(m, "connection"), Distance = Num(m, "distance"),
                        Seed = m.TryGetProperty("seed", out var seed) && seed.TryGetInt64(out long sd) ? sd : null,
                    };
                    break;
                }
                case "T":
                    tileCodes[int.Parse(f[1], CultureInfo.InvariantCulture)] = Need(surface).Tiles.Count;
                    surface!.Tiles.Add(new TileType(f[2], new SKColor(byte.Parse(f[3], CultureInfo.InvariantCulture),
                        byte.Parse(f[4], CultureInfo.InvariantCulture), byte.Parse(f[5], CultureInfo.InvariantCulture))));
                    break;
                case "P":
                {
                    using var doc = JsonDocument.Parse(f[3]);
                    var info = doc.RootElement;
                    protoCodes[int.Parse(f[1], CultureInfo.InvariantCulture)] = Need(surface).Prototypes.Count;
                    surface!.Prototypes.Add(new EntityPrototype(f[2], Str(info, "type") ?? "", Color(info, "map"), Color(info, "friendly"),
                        Color(info, "enemy"), Math.Max(1, (int)Num(info, "w")), Math.Max(1, (int)Num(info, "h"))));
                    break;
                }
                case "C":
                    Need(surface).Chunks[(int.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2], CultureInfo.InvariantCulture))] = DecodeChunk(f[3], tileCodes);
                    break;
                case "E":
                    Need(surface).Entities.Add(new MapEntity(protoCodes[I(f[1])], I(f[2]), F(f[3]), F(f[4]), F(f[5]), F(f[6]),
                        (byte)I(f[7]), F(f[8]), F(f[9]), f.Length > 10 && f[10].Length > 2 ? f[10] : null));
                    break;
                case "R":
                    Need(surface).Resources.Add(new MapPoint(protoCodes[I(f[1])], F(f[2]), F(f[3]), long.Parse(f[4], CultureInfo.InvariantCulture)));
                    break;
                case "N":
                    Need(surface).Nature.Add(new MapPoint(protoCodes[I(f[1])], F(f[2]), F(f[3]), 0, f.Length > 4 ? f[4] : null));
                    break;
                case "W":
                    Need(surface).Wires.Add(new Wire(F(f[1]), F(f[2]), F(f[3]), F(f[4])));
                    break;
                case "G":
                {
                    using var doc = JsonDocument.Parse(f[1]);
                    var t = doc.RootElement;
                    t.TryGetProperty("icon", out var icon);
                    Need(surface).Tags.Add(new ChartTag(Str(t, "text") ?? "", Num(t, "x"), Num(t, "y"),
                        icon.ValueKind == JsonValueKind.Object ? Str(icon, "type") : null, icon.ValueKind == JsonValueKind.Object ? Str(icon, "name") : null,
                        Str(t, "user")));
                    break;
                }
                case "X":
                    Need(surface).Counted[f[1]] = long.Parse(f[2], CultureInfo.InvariantCulture);
                    break;
            }
        }
        return surface ?? throw new SaveFormatException($"The export {Path.GetFileName(file)} is empty.");
    }

    /// <summary>"3*30,5,2*4" → 1024 tile indexes.</summary>
    internal static ushort[] DecodeChunk(string runs, IReadOnlyDictionary<int, int> codes)
    {
        var tiles = new ushort[32 * 32];
        int at = 0;
        foreach (var run in runs.AsSpan().Split(','))
        {
            var token = runs.AsSpan()[run];
            int star = token.IndexOf('*');
            int code = int.Parse(star < 0 ? token : token[..star], CultureInfo.InvariantCulture);
            int count = star < 0 ? 1 : int.Parse(token[(star + 1)..], CultureInfo.InvariantCulture);
            ushort index = (ushort)codes[code];
            for (int i = 0; i < count && at < tiles.Length; i++)
                tiles[at++] = index;
        }
        return tiles;
    }

    private static SurfaceData Need(SurfaceData? surface) => surface ?? throw new SaveFormatException("The export has data before its surface line.");
    private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static SKColor? Color(JsonElement e, string name) =>
        e.TryGetProperty(name, out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() >= 3
            ? new SKColor((byte)c[0].GetInt32(), (byte)c[1].GetInt32(), (byte)c[2].GetInt32())
            : null;
}
