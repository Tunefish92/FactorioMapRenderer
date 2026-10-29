namespace FactorioMapRenderer.World;

public enum LayerKind
{
    /// <summary>Rendered into image tiles (dense geometry).</summary>
    Raster,
    /// <summary>Exported as interactive markers with popups.</summary>
    Marker,
}

public sealed record LayerDef(string Id, string Title, string Group, LayerKind Kind, bool VisibleByDefault, string Color, string Description);

public static class Layers
{
    public static readonly LayerDef Terrain = new("terrain", "Terrain", "Base", LayerKind.Raster, true, "#5a4a32", "The ground in the map view's colours: grass, sand, water, space");
    public static readonly LayerDef Ground = new("ground", "Ground (game textures)", "Base", LayerKind.Raster, true, "#7a6a48", "The ground in the game's own textures, when zoomed in");
    public static readonly LayerDef Resources = new("resources", "Resources", "World", LayerKind.Raster, true, "#69859a", "Ore, stone, coal and the other resources in the ground");
    public static readonly LayerDef Nature = new("nature", "Trees, rocks & cliffs", "World", LayerKind.Raster, true, "#30632f", "Trees, rocks, cliffs and plants");
    public static readonly LayerDef Patches = new("patches", "Resource patches", "World", LayerKind.Marker, false, "#c6a33b", "One marker per patch with its total amount – click for details");
    public static readonly LayerDef Enemies = new("enemies", "Enemy structures", "Enemies", LayerKind.Raster, true, "#ff1919", "Spawners, worms, demolishers and everything else of the enemy force");
    public static readonly LayerDef Nests = new("nests", "Enemy bases", "Enemies", LayerKind.Marker, false, "#e0453a", "One marker per enemy base with its spawners and worms");
    public static readonly LayerDef Buildings = new("buildings", "Buildings", "Factory", LayerKind.Raster, true, "#00618f", "Machines, chests, turrets, walls – click for details");
    public static readonly LayerDef Belts = new("belts", "Belts", "Factory", LayerKind.Raster, true, "#cca047", "Transport belts, underground belts, splitters, loaders");
    public static readonly LayerDef Pipes = new("pipes", "Pipes", "Factory", LayerKind.Raster, true, "#3a8cc4", "Pipes, pumps and heat pipes");
    public static readonly LayerDef Rails = new("rails", "Railways", "Factory", LayerKind.Raster, true, "#8c8c8c", "Rails, elevated rails, signals and train stops");
    public static readonly LayerDef Power = new("power", "Power grid", "Factory", LayerKind.Raster, true, "#00d4ff", "Electric poles and their copper wires");
    public static readonly LayerDef Graphics = new("graphics", "Game graphics", "Factory", LayerKind.Raster, true, "#b08850", "Buildings, belts, pipes, ore, trees, rocks and nests in the game's own graphics, when zoomed in");
    public static readonly LayerDef Players = new("players", "Players", "Markers", LayerKind.Marker, true, "#ff4d4d", "Where each player was when the game was saved");
    public static readonly LayerDef Tags = new("tags", "Map tags", "Markers", LayerKind.Marker, true, "#f2c94c", "The tags placed on the map in the game");
    public static readonly LayerDef Stations = new("stations", "Train stops", "Markers", LayerKind.Marker, true, "#e6e6e6", "Train stops with their names");
    public static readonly LayerDef Vehicles = new("vehicles", "Vehicles & trains", "Markers", LayerKind.Marker, false, "#9b59b6", "Cars, tanks, spidertrons and locomotives");
    public static readonly LayerDef Landmarks = new("landmarks", "Landmarks", "Markers", LayerKind.Marker, true, "#2ecc71", "Rocket silos, cargo landing pads, platform hubs, the crash site");

    public static readonly LayerDef[] All =
    [
        Terrain, Ground, Resources, Nature, Patches, Enemies, Nests, Buildings, Belts, Pipes, Rails, Power, Graphics,
        Players, Tags, Stations, Vehicles, Landmarks,
    ];
}
