using System.Diagnostics;
using System.Text;
using FactorioMapRenderer.Game;
using FactorioMapRenderer.Rendering;
using FactorioMapRenderer.Save;
using FactorioMapRenderer.Serve;
using FactorioMapRenderer.World;

namespace FactorioMapRenderer;

internal static class Program
{
    private const string Usage = """
        Factorio Map Renderer - turns a Factorio save (.zip) into an interactive HTML map.

        Usage:
          FactorioMapRenderer [save.zip] [options]

        Options:
          -o, --output <dir>        Output directory (default: <save name>_map next to the save)
          -z, --max-zoom <5-10>     Highest rendered zoom level (default: 9, 16 px per game tile; 10 is 32 px,
                                    8 is 8 px). Each level less renders about four times faster
              --cache <dir>         Work folder: Factorio's runs, downloads, fetched saves
                                    (default: %LOCALAPPDATA%/FactorioMapRenderer)
          -h, --help                Show this help

        The game (Factorio reads the save; the renderer draws what it reports):
          -g, --game <dir>          A Factorio installation for Linux: the full game or the headless server
                                    (default: found in the usual Steam folders or in ./basegame)
              --download-headless   Without a game that can load the save, download Factorio's free headless
                                    server from factorio.com into the cache
              --mods <dir>          Mods from the mod portal (zip files) that the save uses
              --timeout <minutes>   How long Factorio may take to read a save (default: 30)

        Game data (names, icons and the map view's colours):
              --game-data <dir>     Exported game data (default: gamedata in the current directory). A full game
                                    given with --game is exported once into the cache and used instead when the
                                    exported data is from another game version
              --export-game-data    Export the names and icons of the --game into the --game-data folder and stop

        Server mode (choose the save in the browser: browse, drag and drop, a folder, SSH; RCON for the server):
              --serve [port]        Serve the map at http://localhost:<port>/ (default 8000) and render every save
                                    chosen there; a save file on the command line is optional
              --bind <address>      Address to listen on (default: localhost). Use * to be reachable from other
                                    machines; this requires an admin password
              --admin-password <pw> Only admins who log in with this password see the save game source panel.
                                    Better: set the environment variable FMR_ADMIN_PASSWORD instead

        Open <output>/index.html in a browser afterwards; no web server is needed.
        """;

    private sealed record Options
    {
        public string SavePath { get; set; } = "";
        public string? Output { get; set; }
        public int MaxZoom { get; set; } = 9;
        public string? GameDirectory { get; set; }
        public bool DownloadHeadless { get; set; }
        public string? ModsDirectory { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);
        public string CacheDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactorioMapRenderer");
        public string GameDataDirectory { get; set; } = "gamedata";
        public bool ExportGameData { get; set; }
        public int? ServePort { get; set; }
        public string Bind { get; set; } = MapServer.Localhost;
        public string? AdminPassword { get; set; } = Environment.GetEnvironmentVariable("FMR_ADMIN_PASSWORD") is { Length: > 0 } password ? password : null;
        public bool Help { get; set; }
    }

    private static int Main(string[] args)
    {
        // Save, player and station names can contain non-ASCII characters.
        Console.OutputEncoding = Encoding.UTF8;
        Options options;
        try
        {
            options = Parse(args);
            if (options.Help)
                return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            Run(options);
            return 0;
        }
        catch (Exception ex) when (ex is SaveFormatException or FactorioRunException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not render the save: {ex.Message}");
            return 1;
        }
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        string? savePath = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {arg}.");
            switch (arg)
            {
                case "-h" or "--help" or "/?":
                    Console.WriteLine(Usage);
                    o.Help = true;
                    return o;
                case "-o" or "--output":
                    o.Output = Next();
                    break;
                case "-z" or "--max-zoom":
                    o.MaxZoom = int.TryParse(Next(), out int z) && z is >= 5 and <= 10 ? z : throw new ArgumentException("--max-zoom must be between 5 and 10.");
                    break;
                case "-g" or "--game":
                    o.GameDirectory = Next();
                    break;
                case "--download-headless":
                    o.DownloadHeadless = true;
                    break;
                case "--mods":
                    o.ModsDirectory = Path.GetFullPath(Next());
                    break;
                case "--timeout":
                    o.Timeout = int.TryParse(Next(), out int minutes) && minutes is >= 1 and <= 600
                        ? TimeSpan.FromMinutes(minutes) : throw new ArgumentException("--timeout must be between 1 and 600 minutes.");
                    break;
                case "--cache":
                    o.CacheDirectory = Path.GetFullPath(Next());
                    break;
                case "--game-data":
                    o.GameDataDirectory = Next();
                    break;
                case "--export-game-data":
                    o.ExportGameData = true;
                    break;
                case "--serve":
                    o.ServePort = 8000;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int port))
                    {
                        o.ServePort = port is >= 1 and <= 65535 ? port : throw new ArgumentException("--serve needs a port between 1 and 65535.");
                        i++;
                    }
                    break;
                case "--bind":
                    o.Bind = Next();
                    break;
                case "--admin-password":
                    o.AdminPassword = Next();
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw new ArgumentException($"Unknown option {arg}.");
                    savePath = savePath is null ? arg : throw new ArgumentException("Only one save file can be given.");
                    break;
            }
        }

        if (o.ExportGameData)
        {
            if (o.GameDirectory is null)
                throw new ArgumentException("--export-game-data needs the game (--game <dir>).");
            o.SavePath = savePath is null ? ExportOnly : Path.GetFullPath(savePath);
            if (savePath is null)
                return o;
        }
        if (o.ServePort is not null && o.AdminPassword is null && !MapServer.IsLoopback(o.Bind))
            throw new ArgumentException("Listening on other addresses than localhost needs an admin password (--admin-password or FMR_ADMIN_PASSWORD); " +
                                        "otherwise every visitor could replace the save game.");
        if (o.AdminPassword is { Length: < 8 })
            throw new ArgumentException("The admin password must have at least 8 characters.");
        if (o.ServePort is null)
            savePath ??= FindSaveInCurrentDirectory();
        if (savePath is not null && !File.Exists(savePath))
            throw new ArgumentException($"Save file not found: {savePath}");
        if (o.GameDirectory is not null && FactorioInstallation.TryOpen(o.GameDirectory) is null)
            throw new ArgumentException($"No Factorio installation in {o.GameDirectory} (it needs a data/base folder).");

        if (savePath is null)
        {
            // Server mode without a save: the map is chosen in the browser.
            o.Output = Path.GetFullPath(o.Output ?? "map");
            return o;
        }
        o.SavePath = Path.GetFullPath(savePath);
        o.Output = Path.GetFullPath(o.Output ?? Path.Combine(Path.GetDirectoryName(o.SavePath)!, Path.GetFileNameWithoutExtension(o.SavePath) + "_map"));
        return o;
    }

    /// <summary>Stands in for the save path when only --export-game-data was asked for.</summary>
    private const string ExportOnly = "<export>";

    private static void Run(Options o)
    {
        var total = Stopwatch.StartNew();
        var installed = FindGame(o);
        if (o.ExportGameData)
        {
            GameData.Export(installed!, installed!.PrepareBinary(o.CacheDirectory), Path.Combine(o.CacheDirectory, "run"), o.GameDataDirectory, Console.WriteLine);
            if (o.SavePath == ExportOnly)
                return;
        }
        var game = LoadGameData(o, installed);
        if (o.ServePort is { } port)
        {
            Serve(o, installed, game, port);
            return;
        }
        Render(o, o.SavePath, installed, game);
        Console.WriteLine($"Done in {total.Elapsed.TotalSeconds:0.0}s. Open {Path.Combine(o.Output!, "index.html")}");
    }

    /// <summary>The game given with --game, or one in the usual places.</summary>
    private static FactorioInstallation? FindGame(Options o)
    {
        if (o.GameDirectory is not null)
            return FactorioInstallation.TryOpen(o.GameDirectory);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            "basegame",
            Path.Combine(home, ".steam", "steam", "steamapps", "common", "Factorio"),
            Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Factorio"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps", "common", "Factorio"),
            Path.Combine(home, "factorio"),
            "/opt/factorio",
        ];
        foreach (string candidate in candidates)
            if (Directory.Exists(candidate) && FactorioInstallation.TryOpen(candidate) is { } found)
                return found;
        return null;
    }

    /// <summary>
    /// The exported game data. A full game of another version than the export is exported once into the
    /// cache, so that the map has the icons and names of the game that is actually played.
    /// </summary>
    private static GameData LoadGameData(Options o, FactorioInstallation? installed)
    {
        Console.WriteLine($"Game data from {Path.GetFullPath(o.GameDataDirectory)}");
        var data = GameData.Load(o.GameDataDirectory, Console.WriteLine);
        if (installed is { HasGraphics: true, Binary: not null } && data?.GameVersion != installed.VersionText)
        {
            string cached = Path.Combine(o.CacheDirectory, "gamedata", installed.VersionText);
            var fromCache = GameData.Load(cached, _ => { });
            if (fromCache is null)
            {
                try
                {
                    GameData.Export(installed, installed.PrepareBinary(o.CacheDirectory), Path.Combine(o.CacheDirectory, "run"), cached, Console.WriteLine);
                    fromCache = GameData.Load(cached, Console.WriteLine);
                }
                catch (Exception ex) when (ex is FactorioRunException or InvalidOperationException or IOException)
                {
                    Console.WriteLine($"  Could not export the game data of {installed.Directory}: {ex.Message}");
                }
            }
            data = fromCache ?? data;
        }
        if (data is null)
            Console.WriteLine("  No game data: the map shows the ids instead of the names, and no icons.");
        return data ?? GameData.Empty;
    }

    /// <summary>Serves the output directory and renders whatever save the viewer asks for.</summary>
    private static void Serve(Options o, FactorioInstallation? installed, GameData game, int port)
    {
        // The server's own lines (logins, errors) go to the console only; what a render writes also goes to the
        // pages, which show it while the map is being rendered.
        var console = Console.Out;
        MapServer server = null!;
        void Logged(Action work)
        {
            Console.SetOut(TextWriter.Synchronized(new TeeWriter(console, server.RenderLog)));
            try
            {
                var sw = Stopwatch.StartNew();
                work();
                Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:0.0}s.");
            }
            finally
            {
                Console.SetOut(console);
            }
        }
        server = new MapServer(o.Output!, o.CacheDirectory, port, (savePath, saveName, progress) => Logged(() => Render(o, savePath, installed, game, progress, saveName)),
            console.WriteLine, o.Bind, o.AdminPassword);

        string? renderAgain = null, renderName = null, reason = null;
        if (o.SavePath.Length > 0)
        {
            // The page is there at once and shows how the render of the given save goes.
            if (!File.Exists(Path.Combine(o.Output!, "map-data.js")))
                HtmlExporter.ExportEmpty(o.Output!, o.MaxZoom, game);
            else
                HtmlExporter.RefreshViewer(o.Output!, game);
            (renderAgain, reason) = (o.SavePath, "start");
            server.SetFileSource(o.SavePath);
        }
        else if (!File.Exists(Path.Combine(o.Output!, "map-data.js")))
            HtmlExporter.ExportEmpty(o.Output!, o.MaxZoom, game);
        else
        {
            // What version the map on disk came from, for the viewer's "what is new" window.
            string stamp = Path.Combine(o.Output!, RenderedStamp);
            string renderedBy = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : "";
            if (renderedBy != HtmlExporter.Version)
                File.WriteAllText(Path.Combine(o.Output!, HtmlExporter.UpdatedFromFile), renderedBy);
            HtmlExporter.RefreshViewer(o.Output!, game);
            if (SaveToRenderAgain(o) is var (path, name))
                (renderAgain, renderName) = (path, name);
            reason = "update";
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        // The page is there at once; while the map is rendered it shows how far that has come, with the lines of
        // the console.
        if (renderAgain is not null)
            _ = server.RenderAtStartAsync(reason!, renderName ?? Path.GetFileName(renderAgain), progress => Logged(() =>
            {
                if (reason == "update")
                    Console.WriteLine($"The map on disk was rendered by another version; rendering it again from {renderAgain}");
                Render(o, renderAgain, installed, game, progress, renderName);
            }), stop.Token);
        try
        {
            server.RunAsync(stop.Token).GetAwaiter().GetResult();
        }
        catch (System.Net.HttpListenerException ex)
        {
            Console.Error.WriteLine($"Could not listen on port {port}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>Names the version that rendered a map, so that a newer version knows the map is not its own.</summary>
    private const string RenderedStamp = ".rendered", SourceStamp = ".rendered-from";

    /// <summary>
    /// A map on disk that another version rendered lacks what this version adds. The save it was made from is
    /// usually still in the cache: that one is to be rendered again.
    /// </summary>
    /// <returns>The save the map on disk came from and its own name, if the map was made by another version.</returns>
    private static (string Path, string Name)? SaveToRenderAgain(Options o)
    {
        string stamp = Path.Combine(o.Output!, RenderedStamp);
        if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == HtmlExporter.Version)
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(o.Output!, "map-data.js")), "\"fileName\":\"([^\"]*)\"");
        string name = match.Success ? Path.GetFileName(System.Text.RegularExpressions.Regex.Unescape(match.Groups[1].Value)) : "";
        // the file it was rendered from (a fetched save's snapshot); maps of 0.1.0 only have the save's name
        string source = Path.Combine(o.Output!, SourceStamp);
        string file = File.Exists(source) ? Path.GetFileName(File.ReadAllText(source).Trim()) : name;
        if (file.Length == 0)
            return null;
        string save = Path.Combine(o.CacheDirectory, "saves", file);
        if (File.Exists(save))
            return (save, name.Length > 0 ? name : file);
        Console.WriteLine("The map on disk was rendered by another version; load or refresh the save game to get everything this version shows.");
        return null;
    }

    /// <summary>Writes to the console and hands every finished line to another reader as well.</summary>
    private sealed class TeeWriter(TextWriter inner, Action<string> line) : TextWriter
    {
        private readonly StringBuilder _pending = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            Collect(value);
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            foreach (char c in value ?? "")
                Collect(c);
        }

        public override void WriteLine(string? value) => Write((value ?? "") + "\n");

        private void Collect(char c)
        {
            if (c != '\n')
            {
                _pending.Append(c);
                return;
            }
            line(_pending.ToString().TrimEnd('\r'));
            _pending.Clear();
        }

        public override void Flush() => inner.Flush();
    }

    /// <summary>A Factorio that can load a save of this version: the one found, or the downloaded headless server.</summary>
    private static FactorioInstallation RunnableGame(Options o, FactorioInstallation? installed, SaveHeader header, Action<string, int>? progress)
    {
        var needed = new Version(header.GameVersion.Major, header.GameVersion.Minor, header.GameVersion.Build);
        if (installed is { Binary: not null } && new Version(installed.Version.Major, installed.Version.Minor, installed.Version.Build) >= needed)
            return installed;
        string reason = installed is null ? "No Factorio was found"
            : installed.Binary is null ? $"The Factorio in {installed.Directory} cannot run on this machine (no Linux binary)"
            : $"The save is from Factorio {header.GameVersionText}, newer than the game in {installed.Directory} ({installed.VersionText})";
        if (!o.DownloadHeadless)
            throw new InvalidOperationException($"{reason}. Give a newer game with --game, or allow --download-headless.");
        Console.WriteLine($"  {reason}; using Factorio's headless server.");
        return HeadlessDownloader.EnsureAsync(o.CacheDirectory, header.GameVersion, Console.WriteLine, progress).GetAwaiter().GetResult();
    }

    /// <param name="progress">Told what is being done and how far along the whole render is, in percent.</param>
    /// <param name="saveName">The save's own file name, shown on the map; by default the file's.</param>
    private static void Render(Options o, string savePath, FactorioInstallation? installed, GameData game, Action<string, int>? progress = null,
        string? saveName = null)
    {
        progress?.Invoke("Reading the save game", 2);
        Console.WriteLine($"Loading {savePath}");
        var header = SaveHeader.Read(savePath);
        Console.WriteLine($"  \"{header.Name}\", saved by Factorio {header.GameVersionText}; mods: {string.Join(", ", header.Mods.Select(m => $"{m.Name} {m.Version}"))}");
        header.EnsureReadable();
        if (header.Untested)
            Console.WriteLine($"  Factorio {header.GameVersionText} is newer than this renderer was tested with ({SaveHeader.TestedUpTo.ToString(3)}): " +
                "it most likely works; the map says that it is untested.");

        var factorio = RunnableGame(o, installed, header, progress);
        string binary = factorio.PrepareBinary(o.CacheDirectory);
        string work = Path.Combine(o.CacheDirectory, "run");
        var sw = Stopwatch.StartNew();
        string exported = FactorioRunner.Export(factorio, binary, savePath, header, work, o.ModsDirectory, Console.WriteLine,
            (stage, percent) => progress?.Invoke(stage, 5 + percent * 45 / 100), o.Timeout);
        Console.WriteLine($"  Factorio read the map in {sw.Elapsed.TotalSeconds:0.0}s");

        progress?.Invoke("Finding buildings and resources", 52);
        var export = MapExport.Read(exported, header, saveName ?? Path.GetFileName(savePath), File.GetLastWriteTimeUtc(savePath));
        var surfaces = export.Surfaces.Select(s => SurfaceMap.Build(s, export, game)).OfType<SurfaceMap>()
            .OrderBy(s => s.SortKey).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
            // only space; the next run empties it anyway
        }
        foreach (var s in surfaces)
            Console.WriteLine($"  {s.Title,-40} {s.Data.Chunks.Count,7:N0} chunks {s.Data.Entities.Count,9:N0} entities {s.Data.Resources.Count,9:N0} resource tiles");

        Console.WriteLine($"Rendering to {o.Output}");
        Directory.CreateDirectory(o.Output!);
        string tileDirectory = Path.Combine(o.Output!, "tiles");
        if (Directory.Exists(tileDirectory))
            Directory.Delete(tileDirectory, recursive: true);
        // Drawing takes 55 to 97 % of the bar, shared by the surfaces by their size.
        double totalChunks = Math.Max(1, surfaces.Sum(s => s.Data.Chunks.Count)), done = 0;
        var tiles = new Dictionary<int, Dictionary<string, TileSet>>();
        foreach (var surface in surfaces)
        {
            double share = surface.Data.Chunks.Count / totalChunks, before = done;
            progress?.Invoke($"Drawing the map: {surface.Title}", 55 + (int)(before * 42));
            tiles[surface.Data.Index] = TileRenderer.Render(surface, Path.Combine(tileDirectory, surface.Data.Index.ToString()), o.MaxZoom, game,
                Console.WriteLine, fraction => progress?.Invoke($"Drawing the map: {surface.Title}", 55 + (int)((before + share * fraction) * 42)));
            done += share;
        }
        progress?.Invoke("Writing the page", 98);
        HtmlExporter.Export(export, surfaces, tiles, o.Output!, o.MaxZoom, game);
        File.WriteAllText(Path.Combine(o.Output!, RenderedStamp), HtmlExporter.Version);
        // which file the map came from, to render it again after an update: a fetched save's snapshot
        File.WriteAllText(Path.Combine(o.Output!, SourceStamp), Path.GetFileName(savePath));
    }

    private static string FindSaveInCurrentDirectory()
    {
        var saves = Directory.GetFiles(Environment.CurrentDirectory, "*.zip");
        return saves.Length switch
        {
            1 => saves[0],
            0 => throw new ArgumentException("No save file given and none found in the current directory."),
            _ => throw new ArgumentException("Several .zip files found in the current directory; please choose one."),
        };
    }
}
