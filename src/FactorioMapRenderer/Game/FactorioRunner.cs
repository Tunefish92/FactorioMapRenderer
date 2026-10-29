using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FactorioMapRenderer.Save;

namespace FactorioMapRenderer.Game;

/// <summary>
/// Runs Factorio to read what a save holds. Factorio's level data is not documented and changes with every
/// version, so the game itself loads the save: as a one-tick benchmark, with the renderer's export mod added
/// to the mods the save was played with. The mod writes the map into script-output (see Assets/exporter);
/// the save is never written.
/// </summary>
public static class FactorioRunner
{
    public const string ExportMod = "factorio-map-renderer-export";

    /// <summary>Runs the save and returns the folder with the export (meta.json, one file per surface).</summary>
    /// <param name="workDirectory">Factorio's write-data folder for this run; emptied first.</param>
    /// <param name="modsDirectory">Where mods from the mod portal are kept (zip files or folders), if anywhere.</param>
    public static string Export(FactorioInstallation game, string binary, string savePath, SaveHeader header, string workDirectory,
        string? modsDirectory, Action<string> log, Action<string, int>? progress = null, TimeSpan? timeout = null)
    {
        Prepare(game, workDirectory, header.Mods.Select(m => m.Name), modsDirectory, withExporter: true, log);
        string output = Path.Combine(workDirectory, "script-output", "map-export");

        // Lines of Factorio's log that say how far it has come, in percent of the whole run.
        (string Marker, string Stage, int Percent)[] stages =
        [
            ("Loading mod", "Starting Factorio", 5),
            ("Loading map", "Loading the save in Factorio", 15),
            ("Loading level.dat", "Loading the map", 20),
            ($"__{ExportMod}__/control.lua", "Reading the map from the game", 35),
            ("Applying migration", "Reading the map from the game", 85),
            ("Running update", "Finishing", 95),
        ];
        int reached = -1;
        Run(game, binary, workDirectory, ["--benchmark", Path.GetFullPath(savePath), "--benchmark-ticks", "1", "--benchmark-runs", "1"], line =>
        {
            for (int i = reached + 1; i < stages.Length; i++)
            {
                if (!line.Contains(stages[i].Marker, StringComparison.Ordinal))
                    continue;
                reached = i;
                progress?.Invoke(stages[i].Stage, stages[i].Percent);
                break;
            }
        }, log, timeout ?? TimeSpan.FromMinutes(30));

        string error = Path.Combine(output, "error.txt");
        if (File.Exists(error))
            throw new FactorioRunException($"The export in the game failed: {File.ReadAllText(error).Trim()}");
        if (!File.Exists(Path.Combine(output, "done")))
            throw new FactorioRunException("Factorio ended without exporting the map; see the lines above.");
        return output;
    }

    /// <summary>Writes every prototype as JSON (data-raw-dump.json): icons, chart colours; returns its path.</summary>
    public static string DumpData(FactorioInstallation game, string binary, string workDirectory, Action<string> log)
    {
        Prepare(game, workDirectory, game.BuiltInMods.Keys, modsDirectory: null, withExporter: false, log);
        Run(game, binary, workDirectory, ["--dump-data"], _ => { }, log, TimeSpan.FromMinutes(10));
        string dump = Path.Combine(workDirectory, "script-output", "data-raw-dump.json");
        return File.Exists(dump) ? dump : throw new FactorioRunException("Factorio did not write data-raw-dump.json.");
    }

    /// <summary>
    /// A write-data folder of its own, so that the player's own config, mods and saves are never touched: a
    /// config that points at the game's data, and a mod folder with exactly the mods to load.
    /// </summary>
    private static void Prepare(FactorioInstallation game, string workDirectory, IEnumerable<string> wantedMods, string? modsDirectory,
        bool withExporter, Action<string> log)
    {
        if (Directory.Exists(workDirectory))
            Directory.Delete(workDirectory, recursive: true);
        string mods = Path.Combine(workDirectory, "mods");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(Path.Combine(workDirectory, "config"));
        File.WriteAllText(Path.Combine(workDirectory, "config", "config.ini"),
            $"[path]\nread-data={game.DataDirectory}\nwrite-data={Path.GetFullPath(workDirectory)}\n\n[other]\ncheck-updates=false\nenable-crash-log-uploading=false\n");

        // Built-in mods (space-age, quality, …) are switched on exactly when the save or one of its mods needs them.
        var builtIn = game.BuiltInMods;
        var enabled = new HashSet<string>(StringComparer.Ordinal) { "base" };
        var missing = new List<string>();
        var queue = new Queue<string>(wantedMods);
        while (queue.TryDequeue(out string? name))
        {
            if (!enabled.Add(name) && name != "base")
                continue;
            if (builtIn.TryGetValue(name, out var info))
            {
                foreach (string dependency in info.RequiredMods)
                    queue.Enqueue(dependency);
            }
            else if (name != "base" && !LinkMod(name, modsDirectory, mods, queue))
            {
                enabled.Remove(name);
                missing.Add(name);
            }
        }
        if (missing.Count > 0)
            log($"  Mods of the save that are not available: {string.Join(", ", missing)}. Their buildings are missing from the map" +
                (modsDirectory is null ? "." : $"; put them into {modsDirectory} to show them."));

        var list = builtIn.Keys.Union(enabled).Select(name => new { name, enabled = enabled.Contains(name) }).ToList();
        if (withExporter)
        {
            list.Add(new { name = ExportMod, enabled = true });
            WriteExporter(Path.Combine(mods, ExportMod), game.Version);
        }
        File.WriteAllText(Path.Combine(mods, "mod-list.json"), JsonSerializer.Serialize(new { mods = list }));
    }

    /// <summary>Makes a mod from the mods folder available to the run; its own dependencies are queued.</summary>
    private static bool LinkMod(string name, string? modsDirectory, string runMods, Queue<string> queue)
    {
        if (modsDirectory is null || !Directory.Exists(modsDirectory))
            return false;
        // "name_1.2.3.zip" or a folder "name_1.2.3" / "name"; the newest version wins.
        string? found = Directory.EnumerateFileSystemEntries(modsDirectory)
            .Where(p => Path.GetFileName(p) is var f && (f == name || f.StartsWith(name + "_", StringComparison.Ordinal)
                && Version.TryParse(Path.GetFileNameWithoutExtension(f)[(name.Length + 1)..], out _)))
            .OrderByDescending(p => Path.GetFileName(p).Length > name.Length + 1 && Version.TryParse(Path.GetFileNameWithoutExtension(p)[(name.Length + 1)..], out var v) ? v : new Version())
            .FirstOrDefault();
        if (found is null)
            return false;
        string link = Path.Combine(runMods, Path.GetFileName(found));
        if (Directory.Exists(found))
            Directory.CreateSymbolicLink(link, Path.GetFullPath(found));
        else
            File.CreateSymbolicLink(link, Path.GetFullPath(found));
        foreach (string dependency in ReadDependencies(found))
            queue.Enqueue(dependency);
        return true;
    }

    private static IEnumerable<string> ReadDependencies(string mod)
    {
        string? json = null;
        if (Directory.Exists(mod))
            json = File.Exists(Path.Combine(mod, "info.json")) ? File.ReadAllText(Path.Combine(mod, "info.json")) : null;
        else
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(mod);
            var entry = zip.Entries.FirstOrDefault(e => e.Name == "info.json" && e.FullName.Count(c => c == '/') <= 1);
            if (entry is not null)
            {
                using var reader = new StreamReader(entry.Open());
                json = reader.ReadToEnd();
            }
        }
        if (json is null)
            return [];
        string temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp, json);
            return ModInfo.TryRead(temp)?.RequiredMods.ToList() ?? [];
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static void WriteExporter(string directory, Version gameVersion)
    {
        Directory.CreateDirectory(directory);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string file in new[] { "info.json", "control.lua" })
        {
            using var stream = assembly.GetManifestResourceStream($"Assets/exporter/{file}")
                ?? throw new InvalidOperationException($"Missing embedded exporter file {file}.");
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            // A mod is only loaded by the game version it says it is for.
            if (file == "info.json")
                text = text.Replace("\"factorio_version\": \"2.0\"", $"\"factorio_version\": \"{gameVersion.Major}.{gameVersion.Minor}\"");
            File.WriteAllText(Path.Combine(directory, file), text);
        }
    }

    private static void Run(FactorioInstallation game, string binary, string workDirectory, string[] arguments, Action<string> onLine,
        Action<string> log, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = workDirectory,
        };
        foreach (string arg in new[] { "--config", Path.Combine(workDirectory, "config", "config.ini"), "--mod-directory", Path.Combine(workDirectory, "mods") }
                     // the headless server has no sound, and no option to switch it off
                     .Concat(game.HasGraphics ? ["--disable-audio"] : []).Concat(arguments))
            start.ArgumentList.Add(arg);

        // The game's error lines say most when it fails; after an unknown option it prints that first and then its
        // usage text, so errors are kept apart from the last lines.
        var tail = new Queue<string>();
        var errors = new List<string>();
        void Line(string? line)
        {
            if (line is null)
                return;
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 30)
                    tail.Dequeue();
                if ((line.Contains("Error", StringComparison.Ordinal) || line.Contains("does not exist", StringComparison.Ordinal)) && errors.Count < 20)
                    errors.Add(line.Trim());
            }
            onLine(line);
            // Factorio's own log is long; errors and the export's progress are what matter here.
            if (line.Contains("Error", StringComparison.Ordinal) || line.Contains("Loading map", StringComparison.Ordinal))
                log("  factorio: " + line.Trim());
        }

        log($"  Running Factorio {game.VersionText} from {game.Directory}");
        using var process = Process.Start(start) ?? throw new FactorioRunException("Factorio could not be started.");
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new FactorioRunException($"Factorio did not finish within {timeout.TotalMinutes:0} minutes.");
        }
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string last;
            lock (tail)
                last = string.Join("\n", errors.Count > 0 ? errors.TakeLast(8) : tail.Where(l => l.Trim().Length > 0).TakeLast(8));
            throw new FactorioRunException($"Factorio ended with exit code {process.ExitCode}:\n{last}");
        }
    }
}

/// <summary>Running the game failed; the message tells why.</summary>
public sealed class FactorioRunException(string message) : Exception(message);
