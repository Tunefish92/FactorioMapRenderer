using System.Globalization;
using System.Text.Json;
using FactorioMapRenderer.Game;
using SkiaSharp;

namespace FactorioMapRenderer.World;

public enum ShapeKind : byte { Rect, Line, Sprite }

/// <summary>
/// Something drawn into a raster layer, in tile coordinates; <see cref="Icon"/> is drawn into it when zoomed in. A
/// sprite fills its rectangle with the image <see cref="Icon"/> names (see <see cref="GameData.Sprite"/>).
/// </summary>
public readonly record struct Shape(ShapeKind Kind, float X1, float Y1, float X2, float Y2, SKColor Color, string? Icon = null);

public sealed record Marker(double X, double Y, string Name, string Color, int Legend, IReadOnlyList<string[]> Details,
    string? Icon = null, string? Ring = null, bool Dimmed = false);

public sealed class MarkerLayer
{
    public List<Marker> Markers { get; } = [];
    /// <summary>Label, colour, icon key; the count is taken from the markers.</summary>
    public List<(string Label, string Color, string? Icon)> Legend { get; } = [];

    public int LegendIndex(string label, string color, string? icon)
    {
        int i = Legend.FindIndex(l => l.Label == label);
        if (i >= 0)
            return i;
        Legend.Add((label, color, icon));
        return Legend.Count - 1;
    }
}

/// <summary>A building that shows its details when clicked; it is drawn in the tiles, not as a marker.</summary>
public sealed record Inspectable(double X, double Y, double Radius, string Name, string Group, IReadOnlyList<string[]> Details, string? Icon);

/// <summary>
/// What the game shows over a building in its alt mode: the recipe, the ore being mined, the filters; below them its
/// modules, one icon each. <see cref="Size"/> is the building's smaller side in tiles.
/// </summary>
public sealed record AltInfo(float X, float Y, float Size, IReadOnlyList<string> Icons, IReadOnlyList<string> Modules);

/// <summary>A surface turned into what the map shows: raster shapes, markers, clickable buildings and numbers.</summary>
public sealed class SurfaceMap
{
    public required SurfaceData Data { get; init; }
    public required string Title { get; init; }
    /// <summary>"planet", "platform" or "surface".</summary>
    public required string Kind { get; init; }
    public string? Icon { get; init; }
    /// <summary>Charted area in tiles.</summary>
    public required (int MinX, int MinY, int MaxX, int MaxY) Bounds { get; init; }

    public Dictionary<LayerDef, List<Shape>> Raster { get; } = [];
    public Dictionary<LayerDef, MarkerLayer> Markers { get; } = [];
    public List<Inspectable> Inspect { get; } = [];
    public List<AltInfo> Alt { get; } = [];
    public List<(string Name, long Count, string? Icon)> Counts { get; } = [];
    public List<(string Name, double Count, string? Icon)> Production { get; } = [];

    private static readonly HashSet<string> BeltTypes = ["transport-belt", "underground-belt", "splitter", "loader", "loader-1x1", "linked-belt", "lane-splitter"];
    private static readonly HashSet<string> PipeTypes = ["pipe", "pipe-to-ground", "pump", "infinity-pipe", "heat-pipe", "valve"];
    private static readonly HashSet<string> RailTypes =
    [
        "straight-rail", "curved-rail-a", "curved-rail-b", "half-diagonal-rail", "legacy-straight-rail", "legacy-curved-rail",
        "elevated-straight-rail", "elevated-curved-rail-a", "elevated-curved-rail-b", "elevated-half-diagonal-rail", "rail-ramp", "rail-support",
        "rail-signal", "rail-chain-signal", "train-stop",
    ];
    private static readonly HashSet<string> VehicleTypes = ["car", "spider-vehicle", "locomotive", "cargo-wagon", "fluid-wagon", "artillery-wagon", "infinity-cargo-wagon"];
    private static readonly HashSet<string> LandmarkTypes = ["rocket-silo", "cargo-landing-pad", "space-platform-hub"];
    /// <summary>Not drawn: players and vehicles are markers, the rest is invisible on the map.</summary>
    private static readonly HashSet<string> NotDrawn = ["character", "cargo-pod", "spider-leg", "simple-entity-with-owner", "rocket-silo-rocket", "rocket-silo-rocket-shadow"];
    private static readonly string[] PlanetOrder = ["nauvis", "vulcanus", "fulgora", "gleba", "aquilo"];

    public int SortKey => Kind == "planet" ? (Array.IndexOf(PlanetOrder, Data.Planet) is var i and >= 0 ? i : 50) : Kind == "platform" ? 100 : 200;

    public static SurfaceMap? Build(SurfaceData s, MapExport export, GameData game)
    {
        if (s.Chunks.Count == 0)
            return null;
        int minX = s.Chunks.Keys.Min(c => c.X) * 32, minY = s.Chunks.Keys.Min(c => c.Y) * 32;
        int maxX = (s.Chunks.Keys.Max(c => c.X) + 1) * 32, maxY = (s.Chunks.Keys.Max(c => c.Y) + 1) * 32;
        string kind = s.Planet is not null ? "planet" : s.IsPlatform ? "platform" : "surface";
        var map = new SurfaceMap
        {
            Data = s, Kind = kind, Bounds = (minX, minY, maxX, maxY),
            Title = s.Planet is { } planet ? game.Name("space-location", planet) : s.Platform ?? GameData.Readable(s.Name),
            Icon = s.Planet is { } p ? game.IconKey("space-location", p) : s.IsPlatform ? game.IconKey("item", "space-platform-foundation") ?? game.IconKey("entity", "space-platform-hub") : null,
        };
        foreach (var layer in Layers.All)
        {
            if (layer.Kind == LayerKind.Raster)
                map.Raster[layer] = [];
            else
                map.Markers[layer] = new MarkerLayer();
        }
        map.Fill(export, game);
        return map;
    }

    private void Fill(MapExport export, GameData game)
    {
        var s = Data;
        var chart = game.Chart;
        var forces = export.Forces.ToDictionary(f => f.Index, f => f.Name);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        // ---- resources and nature
        foreach (var r in s.Resources)
        {
            var proto = s.Prototypes[r.Prototype];
            var color = proto.Map ?? proto.Friendly ?? new SKColor(160, 160, 160);
            // Fluid wells and other large resources are drawn at their size, ore tile by tile.
            float half = proto.Width > 1 ? proto.Width / 2f : 0.5f;
            Raster[Layers.Resources].Add(new Shape(ShapeKind.Rect, r.X - half, r.Y - half, r.X + half, r.Y + half, color));
        }
        foreach (var n in s.Nature)
        {
            var proto = s.Prototypes[n.Prototype];
            counts[proto.Name] = counts.GetValueOrDefault(proto.Name) + 1;
            if (proto.Type == "fish")
                continue;
            var color = proto.Map ?? chart.ByType.GetValueOrDefault(proto.Type, new SKColor(90, 90, 90));
            if (proto.Type == "tree")
                color = color.WithAlpha(150);
            float hw = Math.Max(0.5f, proto.Width / 2f), hh = Math.Max(0.5f, proto.Height / 2f);
            Raster[Layers.Nature].Add(new Shape(ShapeKind.Rect, n.X - hw, n.Y - hh, n.X + hw, n.Y + hh, color));
        }
        PatchMarkers(game);

        // ---- entities
        var nests = new List<MapEntity>();
        foreach (var e in s.Entities)
        {
            var proto = s.Prototypes[e.Prototype];
            counts[proto.Name] = counts.GetValueOrDefault(proto.Name) + 1;
            string force = forces.GetValueOrDefault(e.Force, "player");
            bool enemy = force == "enemy";
            using var details = e.Details is null ? null : JsonDocument.Parse(e.Details);
            var d = details?.RootElement ?? default;

            if (enemy)
            {
                Raster[Layers.Enemies].Add(Rect(e, proto.Enemy ?? chart.DefaultEnemy));
                nests.Add(e);
                continue;
            }
            if (VehicleTypes.Contains(proto.Type))
            {
                if (proto.Type is "car" or "spider-vehicle" or "locomotive")
                    AddMarker(Layers.Vehicles, e.X, e.Y, MapExport.Str(d, "name") ?? game.Name("entity", proto.Name), game.Name("entity", proto.Name),
                        game.IconKey("entity", proto.Name), Describe(d, proto, game));
                // trains are drawn on the rails
                if (proto.Type != "car" && proto.Type != "spider-vehicle")
                    Raster[Layers.Rails].Add(Rect(e, proto.Friendly ?? chart.DefaultFriendly));
                continue;
            }
            if (NotDrawn.Contains(proto.Type))
                continue;

            if (proto.Type == "train-stop")
                AddMarker(Layers.Stations, e.X, e.Y, MapExport.Str(d, "name") ?? game.Name("entity", proto.Name), "Train stop",
                    game.IconKey("entity", proto.Name), Describe(d, proto, game));
            if (LandmarkTypes.Contains(proto.Type) || proto.Name.StartsWith("crash-site-spaceship", StringComparison.Ordinal))
                AddMarker(Layers.Landmarks, e.X, e.Y, game.Name("entity", proto.Name), game.Name("entity", proto.Name),
                    game.IconKey("entity", proto.Name), Describe(d, proto, game));

            LayerDef layer;
            SKColor color;
            if (BeltTypes.Contains(proto.Type))
                (layer, color) = (Layers.Belts, proto.Friendly ?? chart.FriendlyByType.GetValueOrDefault(proto.Type, chart.DefaultFriendly));
            else if (PipeTypes.Contains(proto.Type))
                (layer, color) = (Layers.Pipes, proto.Friendly ?? chart.DefaultFriendly);
            else if (RailTypes.Contains(proto.Type))
                (layer, color) = (Layers.Rails, proto.Type.StartsWith("elevated", StringComparison.Ordinal) || proto.Type == "rail-support" ? chart.ElevatedRail
                    : proto.Type == "rail-ramp" ? chart.RailRamp
                    : proto.Type.Contains("rail", StringComparison.Ordinal) && !proto.Type.Contains("signal", StringComparison.Ordinal) ? proto.Map ?? chart.Rail
                    : proto.Friendly ?? chart.DefaultFriendly);
            else if (proto.Type is "electric-pole" or "power-switch")
                (layer, color) = (Layers.Power, chart.ElectricPole);
            else if (force == "neutral")
                (layer, color) = (Layers.Buildings, proto.Map ?? new SKColor(120, 120, 120));
            else
                (layer, color) = (Layers.Buildings, proto.Friendly ?? chart.FriendlyByType.GetValueOrDefault(proto.Type, chart.DefaultFriendly));

            // Larger buildings carry their icon when zoomed in.
            string? icon = layer == Layers.Buildings && Math.Min(e.X2 - e.X1, e.Y2 - e.Y1) >= 1.8f ? game.IconKey("entity", proto.Name) : null;
            Raster[layer].Add(Rect(e, color, icon));
            if ((AltIcons(d, game), Modules(d, game)) is var (alt, modules) && alt.Count + modules.Count > 0)
                Alt.Add(new AltInfo(e.X, e.Y, Math.Min(e.X2 - e.X1, e.Y2 - e.Y1), alt, modules));

            if (e.Details is not null && proto.Type != "train-stop" && !BeltTypes.Contains(proto.Type))
                Inspect.Add(new Inspectable(e.X, e.Y, Math.Max(e.X2 - e.X1, e.Y2 - e.Y1) / 2, game.Name("entity", proto.Name), layer.Title,
                    Describe(d, proto, game), game.IconKey("entity", proto.Name)));
        }
        foreach (var w in s.Wires)
            Raster[Layers.Power].Add(new Shape(ShapeKind.Line, w.X1, w.Y1, w.X2, w.Y2, chart.CopperWire));
        NestMarkers(nests, game);
        if (game.HasGraphics)
            GameGraphics(forces, game);

        // ---- players and map tags
        foreach (var p in export.Players.Where(p => p.Surface == s.Index))
        {
            var details = new List<string[]>
            {
                new[] { "Status", p.Connected ? "Online" : "Offline" },
                new[] { "Played", Duration(p.OnlineTicks) },
            };
            if (p.Admin)
                details.Add(["Role", "Admin"]);
            if (!p.Connected && export.Tick > 0 && p.LastOnlineTick > 0)
                details.Add(["Last online", $"{Duration(export.TicksPlayed - p.LastOnlineTick)} before the save"]);
            string ring = p.Color is { } c ? $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}" : "#ff4d4d";
            var markers = Markers[Layers.Players];
            int legend = markers.LegendIndex(p.Connected ? "Online" : "Offline", p.Connected ? "#2ecc71" : "#95a5a6", null);
            markers.Markers.Add(new Marker(p.X, p.Y, p.Name, ring, legend, details, null, ring, !p.Connected));
        }
        foreach (var tag in s.Tags)
        {
            string kind = tag.IconType switch { "virtual" => "virtual-signal", null => "item", var t => t };
            string? icon = tag.IconName is null ? null : game.IconKey(kind, tag.IconName);
            string name = tag.Text.Length > 0 ? tag.Text : tag.IconName is null ? "Map tag" : game.Name(kind, tag.IconName);
            var details = new List<string[]>();
            if (tag.User is not null)
                details.Add(["Placed by", tag.User]);
            AddMarker(Layers.Tags, tag.X, tag.Y, name, "Map tag", icon, details);
        }

        // ---- numbers
        foreach (var (name, count) in s.Counted)
            counts[name] = counts.GetValueOrDefault(name) + count;
        foreach (var (name, count) in counts.OrderByDescending(c => c.Value))
            Counts.Add((game.Name("entity", name), count, game.IconKey("entity", name)));
        foreach (var (name, count) in s.ItemsProduced.Select(p => (p.Key, p.Value, "item")).Concat(s.FluidsProduced.Select(p => (p.Key, p.Value, "fluid")))
                     .OrderByDescending(p => p.Value).Select(p => (p.Item1, p.Item2)))
        {
            string kind = s.FluidsProduced.ContainsKey(name) && !s.ItemsProduced.ContainsKey(name) ? "fluid" : "item";
            Production.Add((game.Name(kind, name), count, game.IconKey(kind, name)));
        }
    }

    /// <summary>Lie flat on the ground: drawn before everything that stands on it.</summary>
    private static readonly HashSet<string> FlatTypes = ["transport-belt", "underground-belt", "splitter", "lane-splitter", "loader", "loader-1x1", "linked-belt", "pipe", "pipe-to-ground", "heat-pipe"];

    /// <summary>
    /// The "game graphics" layer: everything that has a sprite, as the game draws it. Ore first, then what lies flat
    /// (belts, pipes), then the rest from back to front, so that tall buildings stand in front of what is behind them.
    /// Buildings without a sprite are their footprint with their icon.
    /// </summary>
    /// <summary>Copper wire as the game draws it (the map view shows it in its own colour).</summary>
    private static readonly SKColor WireColor = new(206, 124, 70);

    private void GameGraphics(Dictionary<int, string> forces, GameData game)
    {
        var s = Data;
        var layer = Raster[Layers.Graphics];
        foreach (var r in s.Resources)
        {
            var proto = s.Prototypes[r.Prototype];
            var stages = game.ResourceStages(proto.Name);
            int stage = 0;
            while (stage < stages.Count - 1 && r.Amount < stages[stage])
                stage++;
            if (Sprite(game, proto.Name, r.X, r.Y, $"s{stage}-v{Variation(r.X, r.Y)}", $"s{stage}-v0") is { } shape)
                layer.Add(shape);
        }

        // pipes take their shape from the pipes next to them
        var pipes = new HashSet<(int, int)>();
        foreach (var e in s.Entities)
            if (s.Prototypes[e.Prototype].Type is "pipe" or "pipe-to-ground" or "pump" or "infinity-pipe")
                pipes.Add(((int)MathF.Floor(e.X), (int)MathF.Floor(e.Y)));

        var flat = new List<Shape>();
        var standing = new List<(float Y, Shape Shape)>();
        // rails part by part (beds, ties, metals), the ones on the ground under everything, elevated ones over it
        var rails = Game.SpriteExport.RailParts.Select(_ => new List<Shape>()).ToArray();
        var elevated = Game.SpriteExport.RailParts.Select(_ => new List<Shape>()).ToArray();
        // inserter arms reach over the buildings they work with
        var arms = new List<Shape>();
        foreach (var n in s.Nature)
        {
            var proto = s.Prototypes[n.Prototype];
            if (proto.Type == "fish")
                continue;
            // a cliff by the way it runs ("west-to-east" in the save, "west_to_east" in the game data)
            string prefix = n.Variant is { } orientation ? orientation.Replace('-', '_') + "-" : "";
            var shape = Sprite(game, proto.Name, n.X, n.Y, $"{prefix}v{Variation(n.X, n.Y)}", $"{prefix}v0");
            if (shape is null && proto.Type == "cliff")
            {
                // without its picture as in the map view, so that it is there when the map view gives way
                float hw = Math.Max(0.5f, proto.Width / 2f), hh = Math.Max(0.5f, proto.Height / 2f);
                shape = new Shape(ShapeKind.Rect, n.X - hw, n.Y - hh, n.X + hw, n.Y + hh, proto.Map ?? new SKColor(90, 90, 90));
            }
            if (shape is { } drawn)
                standing.Add((n.Y, drawn));
        }
        foreach (var e in s.Entities)
        {
            var proto = s.Prototypes[e.Prototype];
            if (proto.Type is "car" or "spider-vehicle" || NotDrawn.Contains(proto.Type))
                continue;
            if (VehicleTypes.Contains(proto.Type))
            {
                // trains stand on the rails in any angle: their footprint with their icon
                standing.Add((e.Y2, new Shape(ShapeKind.Rect, e.X1, e.Y1, e.X2, e.Y2, new SKColor(58, 54, 46, 230), game.IconKey("entity", proto.Name))));
                continue;
            }
            string exact = e.Direction.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (RailTypes.Contains(proto.Type) && game.Sprite(proto.Name, $"{exact}-{Game.SpriteExport.RailParts[0]}", exact: true) is not null)
            {
                var into = proto.Type.StartsWith("elevated-", StringComparison.Ordinal) ? elevated : rails;
                for (int part = 0; part < into.Length; part++)
                    if (Sprite(game, proto.Name, e.X, e.Y, $"{exact}-{Game.SpriteExport.RailParts[part]}") is { } piece && piece.Icon!.EndsWith(Game.SpriteExport.RailParts[part], StringComparison.Ordinal))
                        into[part].Add(piece);
                continue;
            }
            if (proto.Type is "rail-signal" or "rail-chain-signal")
            {
                if (Sprite(game, proto.Name, e.X, e.Y, exact) is { } post)
                    standing.Add((e.Y2, post));
                continue;
            }
            string direction = (e.Direction / 4 * 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string variant = direction;
            if (proto.Type == "pipe")
            {
                int x = (int)MathF.Floor(e.X), y = (int)MathF.Floor(e.Y);
                int mask = (pipes.Contains((x, y - 1)) ? 1 : 0) | (pipes.Contains((x + 1, y)) ? 2 : 0) | (pipes.Contains((x, y + 1)) ? 4 : 0) | (pipes.Contains((x - 1, y)) ? 8 : 0);
                variant = $"pipe-{mask}";
            }
            else if (e.Details is not null && proto.Type == "inserter")
            {
                // the arm towards where it picks up
                using var details = JsonDocument.Parse(e.Details);
                if (details.RootElement.TryGetProperty("pickup", out var pickup) && pickup.GetArrayLength() >= 2)
                {
                    double px = pickup[0].GetDouble(), py = pickup[1].GetDouble();
                    variant = Math.Abs(px) > Math.Abs(py) ? (px > 0 ? "4" : "12") : (py > 0 ? "8" : "0");
                }
            }
            else if (e.Details is not null && proto.Type is "underground-belt" or "loader" or "loader-1x1")
            {
                using var details = JsonDocument.Parse(e.Details);
                variant = $"{(MapExport.Str(details.RootElement, "io") == "output" ? "out" : "in")}-{direction}";
            }
            if (proto.Type == "inserter" && Sprite(game, proto.Name, e.X, e.Y, $"{variant}-{Game.SpriteExport.ArmVariant}") is { } arm
                && arm.Icon!.EndsWith(Game.SpriteExport.ArmVariant, StringComparison.Ordinal))
                arms.Add(arm);
            // on a space platform the game's picture for platforms first (cargo bays)
            var shape = s.IsPlatform
                ? Sprite(game, proto.Name, e.X, e.Y, Game.SpriteExport.PlatformVariant, variant, direction)
                : Sprite(game, proto.Name, e.X, e.Y, variant, direction);
            if (shape is null)
            {
                // no sprite (e.g. logistic chests): the footprint with the building's icon
                if (forces.GetValueOrDefault(e.Force) == "enemy")
                    continue;
                shape = new Shape(ShapeKind.Rect, e.X1, e.Y1, e.X2, e.Y2, new SKColor(58, 54, 46, 230), game.IconKey("entity", proto.Name));
            }
            if (FlatTypes.Contains(proto.Type))
                flat.Add(shape.Value);
            else
                standing.Add((e.Y2, shape.Value));
        }
        foreach (var part in rails)
            layer.AddRange(part);
        layer.AddRange(flat);
        layer.AddRange(standing.OrderBy(p => p.Y).Select(p => p.Shape));
        layer.AddRange(arms);
        foreach (var part in elevated)
            layer.AddRange(part);

        // copper wires over everything, from the top of one pole to the top of the next, as the game hangs them
        var tops = new Dictionary<(float, float), float>();
        foreach (var e in s.Entities)
            if (s.Prototypes[e.Prototype].Type == "electric-pole" && game.Sprite(s.Prototypes[e.Prototype].Name, "0") is { Box: var box })
                tops[(e.X, e.Y)] = box.Top + box.Height * 0.12f;
        foreach (var w in s.Wires)
            layer.Add(new Shape(ShapeKind.Line, w.X1, w.Y1 + tops.GetValueOrDefault((w.X1, w.Y1)), w.X2, w.Y2 + tops.GetValueOrDefault((w.X2, w.Y2)), WireColor));
    }

    /// <summary>The sprite of an entity at its position, in the first of the variants the game data has.</summary>
    private static Shape? Sprite(GameData game, string entity, float x, float y, params string[] variants)
    {
        // the variants as asked, then a picture that is the same in every direction
        var found = variants.Select(v => game.Sprite(entity, v, exact: true)).FirstOrDefault(f => f is not null) ?? game.Sprite(entity, "0");
        if (found is not { } sprite)
            return null;
        var (key, box) = sprite;
        return new Shape(ShapeKind.Sprite, x + box.Left, y + box.Top, x + box.Left + box.Width, y + box.Top + box.Height, SKColors.White, key);
    }

    /// <summary>Which of the variations of a tree, rock or ore a position shows: always the same for the same place.</summary>
    private static int Variation(float x, float y)
    {
        uint h = (uint)((int)MathF.Floor(x) * 73856093 ^ (int)MathF.Floor(y) * 19349663);
        h ^= h >> 13;
        h *= 0x5bd1e995;
        return (int)(h % Game.SpriteExport.Variations);
    }

    private static Shape Rect(MapEntity e, SKColor color, string? icon = null) => new(ShapeKind.Rect, e.X1, e.Y1, e.X2, e.Y2, color, icon);

    private void AddMarker(LayerDef layer, double x, double y, string name, string legendLabel, string? icon, IReadOnlyList<string[]> details)
    {
        var markers = Markers[layer];
        int legend = markers.LegendIndex(legendLabel, layer.Color, icon);
        markers.Markers.Add(new Marker(x, y, name, layer.Color, legend, details, icon));
    }

    private static List<string> AltIcons(JsonElement d, GameData game)
    {
        var icons = new List<string>();
        if (d.ValueKind != JsonValueKind.Object)
            return icons;
        if (MapExport.Str(d, "recipe") is { } recipe && (game.IconKey("recipe", recipe) ?? game.IconKey("item", recipe)) is { } r)
            icons.Add(r);
        if (MapExport.Str(d, "mining") is { } mining && game.IconKey("entity", mining) is { } m)
            icons.Add(m);
        if (d.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Array)
            foreach (var f in filters.EnumerateArray())
                if (f.ValueKind == JsonValueKind.String && (game.IconKey("item", f.GetString()!) ?? game.IconKey("fluid", f.GetString()!)) is { } i)
                    icons.Add(i);
        return icons;
    }

    /// <summary>The icon of each module in a machine or beacon, as often as it is there.</summary>
    private static List<string> Modules(JsonElement d, GameData game)
    {
        var icons = new List<string>();
        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("modules", out var modules) && modules.ValueKind == JsonValueKind.Array)
            foreach (var m in modules.EnumerateArray())
                if (game.IconKey("item", m[0].GetString() ?? "") is { } icon)
                    icons.AddRange(Enumerable.Repeat(icon, (int)Math.Clamp(m[1].GetDouble(), 1, 8)));
        return icons;
    }

    /// <summary>What a click on a building shows, from the details the export wrote.</summary>
    private static List<string[]> Describe(JsonElement d, EntityPrototype proto, GameData game)
    {
        var rows = new List<string[]>();
        if (d.ValueKind != JsonValueKind.Object)
            return rows;
        if (MapExport.Str(d, "name") is { } backer && proto.Type != "train-stop")
            rows.Add(["Name", backer]);
        if (MapExport.Str(d, "recipe") is { } recipe)
            rows.Add(["Recipe", game.Name("recipe", recipe)]);
        if (MapExport.Str(d, "mining") is { } mining)
            rows.Add(["Mining", game.Name("entity", mining)]);
        if (d.TryGetProperty("modules", out var modules) && modules.ValueKind == JsonValueKind.Array)
            rows.Add(["Modules", string.Join(", ", modules.EnumerateArray().Select(m => $"{m[1].GetDouble():N0} × {game.Name("item", m[0].GetString() ?? "")}"))]);
        if (d.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Array)
            rows.Add(["Filters", string.Join(", ", filters.EnumerateArray().Select(f => game.Name("item", f.GetString() ?? "")))]);
        if (MapExport.Str(d, "status") is { } status)
            rows.Add(["Status", GameData.Readable(status)]);
        if (MapExport.Str(d, "quality") is { } quality)
            rows.Add(["Quality", game.Name("quality", quality)]);
        if (d.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Number)
            rows.Add(["Train limit", limit.GetInt64().ToString(CultureInfo.InvariantCulture)]);
        if (MapExport.Str(d, "player") is { } player)
            rows.Add(["Player", player]);
        if (d.TryGetProperty("fluids", out var fluids) && fluids.ValueKind == JsonValueKind.Array)
            foreach (var f in fluids.EnumerateArray())
                rows.Add([game.Name("fluid", f[0].GetString() ?? ""), $"{f[1].GetDouble():N0}"]);
        if (d.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var list = items.EnumerateArray().ToList();
            foreach (var item in list.Take(8))
            {
                string itemQuality = item.GetArrayLength() > 2 && item[2].ValueKind == JsonValueKind.String ? $" ({game.Name("quality", item[2].GetString()!)})" : "";
                rows.Add([game.Name("item", item[0].GetString() ?? "") + itemQuality, $"{item[1].GetDouble():N0}"]);
            }
            if (list.Count > 8)
                rows.Add(["…", $"{list.Count - 8} more kinds of items"]);
        }
        return rows;
    }

    // ------------------------------------------------------------------ clusters

    /// <summary>One marker per resource patch: resource tiles of one kind that lie close together.</summary>
    private void PatchMarkers(GameData game)
    {
        var markers = Markers[Layers.Patches];
        foreach (var group in Data.Resources.GroupBy(r => r.Prototype))
        {
            var proto = Data.Prototypes[group.Key];
            var points = group.ToList();
            // Ore lies tile by tile; wells (oil, sulfuric acid, lithium brine…) lie apart from each other.
            var clusters = Cluster(points, p => (p.X, p.Y), 3);
            if (clusters.Count > 3 && clusters.Count(c => c.Count == 1) > clusters.Count / 2)
                clusters = Cluster(points, p => (p.X, p.Y), 16);
            string color = Hex(proto.Map ?? new SKColor(160, 160, 160));
            string name = game.Name("entity", proto.Name);
            string? icon = game.IconKey("entity", proto.Name);
            int legend = markers.LegendIndex(name, color, icon);
            foreach (var cluster in clusters)
            {
                long total = cluster.Sum(p => p.Amount);
                bool wells = proto.Width > 1;
                var details = new List<string[]>
                {
                    new[] { wells ? "Wells" : "Tiles", cluster.Count.ToString("N0", CultureInfo.InvariantCulture) },
                    new[] { "Amount", Amount(total) },
                };
                markers.Markers.Add(new Marker(cluster.Average(p => p.X), cluster.Average(p => p.Y), name, color, legend, details, icon));
            }
        }
    }

    /// <summary>One marker per enemy base: spawners and worms close together.</summary>
    private void NestMarkers(List<MapEntity> enemies, GameData game)
    {
        var markers = Markers[Layers.Nests];
        foreach (var cluster in Cluster(enemies, e => (e.X, e.Y), 20))
        {
            var kinds = cluster.GroupBy(e => Data.Prototypes[e.Prototype].Name).OrderByDescending(g => g.Count()).ToList();
            var main = Data.Prototypes[kinds[0].First().Prototype];
            bool spawners = cluster.Any(e => Data.Prototypes[e.Prototype].Type == "unit-spawner");
            string label = spawners ? "Base with spawners" : main.Type == "turret" ? "Worms" : game.Name("entity", main.Name);
            int legend = markers.LegendIndex(label, spawners ? "#e0453a" : "#e67e22", null);
            var details = kinds.Select(g => new[] { game.Name("entity", g.Key), g.Count().ToString(CultureInfo.InvariantCulture) }).ToList();
            var iconProto = cluster.Select(e => Data.Prototypes[e.Prototype]).FirstOrDefault(p => p.Type == "unit-spawner") ?? main;
            string? icon = game.IconKey("entity", iconProto.Name);
            markers.Markers.Add(new Marker(cluster.Average(e => e.X), cluster.Average(e => e.Y),
                spawners ? $"Enemy base ({cluster.Count})" : label, spawners ? "#e0453a" : "#e67e22", legend, details, icon));
        }
    }

    /// <summary>Groups points that are connected by steps of at most <paramref name="distance"/> tiles.</summary>
    internal static List<List<T>> Cluster<T>(IReadOnlyList<T> points, Func<T, (float X, float Y)> position, float distance)
    {
        var parent = Enumerable.Range(0, points.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < points.Count; i++)
        {
            var (x, y) = position(points[i]);
            var cell = ((int)MathF.Floor(x / distance), (int)MathF.Floor(y / distance));
            if (!grid.TryGetValue(cell, out var list))
                grid[cell] = list = [];
            list.Add(i);
        }
        float d2 = distance * distance;
        foreach (var ((cx, cy), list) in grid)
        {
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (!grid.TryGetValue((cx + dx, cy + dy), out var other))
                    continue;
                foreach (int a in list)
                {
                    var (ax, ay) = position(points[a]);
                    foreach (int b in other)
                    {
                        if (b <= a)
                            continue;
                        var (bx, by) = position(points[b]);
                        if ((ax - bx) * (ax - bx) + (ay - by) * (ay - by) <= d2)
                            parent[Find(a)] = Find(b);
                    }
                }
            }
        }
        return Enumerable.Range(0, points.Count).GroupBy(Find).Select(g => g.Select(i => points[i]).ToList()).ToList();
    }

    public static string Hex(SKColor c) => $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";

    /// <summary>Ticks (60 per second) as "3 d 4 h" or "5 h 07 min".</summary>
    public static string Duration(long ticks)
    {
        long minutes = Math.Max(0, ticks) / 3600;
        long hours = minutes / 60;
        return hours >= 24 ? $"{hours / 24} d {hours % 24} h" : $"{hours} h {minutes % 60:00} min";
    }

    public static string Amount(double value) => value switch
    {
        >= 1e9 => $"{value / 1e9:0.0}B",
        >= 1e6 => $"{value / 1e6:0.0}M",
        >= 1e4 => $"{value / 1e3:0}k",
        _ => value.ToString("N0", CultureInfo.InvariantCulture),
    };
}
