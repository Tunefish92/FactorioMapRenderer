using System.Text.Json;

namespace FactorioMapRenderer.Game;

/// <summary>
/// A Factorio that can be run: the full game (Steam, standalone) or the headless server. Only Linux builds can
/// run here; the Windows and macOS game can still give its graphics and names (<see cref="GameData"/>).
/// </summary>
/// <param name="Directory">The folder with bin/ and data/.</param>
/// <param name="Binary">The Linux executable, or null when this installation cannot run here.</param>
public sealed record FactorioInstallation(string Directory, string DataDirectory, string? Binary, Version Version)
{
    /// <summary>The headless server has no graphics; icons then come from the bundled game data.</summary>
    public bool HasGraphics => System.IO.Directory.Exists(Path.Combine(DataDirectory, "base", "graphics", "icons"));

    public string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";

    /// <summary>The mods that come with the game (base, space-age, quality, …): folders in data/ with an info.json.</summary>
    public IReadOnlyDictionary<string, ModInfo> BuiltInMods => _builtIn ??= System.IO.Directory.EnumerateDirectories(DataDirectory)
        .Select(dir => ModInfo.TryRead(Path.Combine(dir, "info.json")))
        .OfType<ModInfo>()
        .Where(m => m.Name != "core")
        .ToDictionary(m => m.Name, StringComparer.Ordinal);

    private IReadOnlyDictionary<string, ModInfo>? _builtIn;

    /// <summary>Reads the installation in <paramref name="directory"/>, or returns null if there is none.</summary>
    public static FactorioInstallation? TryOpen(string directory)
    {
        directory = Path.GetFullPath(directory);
        // A Steam library folder or the folder above the game is also accepted.
        foreach (string candidate in new[] { directory, Path.Combine(directory, "factorio"), Path.Combine(directory, "Factorio") })
        {
            string data = Path.Combine(candidate, "data");
            if (ModInfo.TryRead(Path.Combine(data, "base", "info.json")) is not { } baseMod)
                continue;
            return new FactorioInstallation(candidate, data, FindBinary(candidate), System.Version.Parse(baseMod.Version));
        }
        return null;
    }

    private static string? FindBinary(string directory)
    {
        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        // The Steam build keeps the real binary in x64_ and a launcher script in x64.
        string[] candidates = arch == "arm64"
            ? ["bin/arm64/factorio"]
            : ["bin/x64_/factorio", "bin/x64/factorio"];
        return candidates.Select(c => Path.Combine(directory, c)).FirstOrDefault(IsElf);
    }

    private static bool IsElf(string path)
    {
        if (!File.Exists(path))
            return false;
        Span<byte> magic = stackalloc byte[4];
        using var file = File.OpenRead(path);
        return file.Read(magic) == 4 && magic.SequenceEqual((ReadOnlySpan<byte>)[0x7F, (byte)'E', (byte)'L', (byte)'F']);
    }

    /// <summary>
    /// The binary to start. A game copied from another machine often lost its executable bit, and a mounted
    /// game is read-only: then the binary (with the Steam library next to it) is copied into the cache. It
    /// finds the game's data through the config the runner writes, not next to itself.
    /// </summary>
    public string PrepareBinary(string cacheDirectory)
    {
        if (Binary is null)
            throw new InvalidOperationException($"The Factorio in {Directory} has no Linux binary for this machine.");
        if (OperatingSystem.IsWindows())
            return Binary;
        if ((File.GetUnixFileMode(Binary) & UnixFileMode.UserExecute) != 0 && CanExecute(Binary))
            return Binary;

        string target = Path.Combine(cacheDirectory, "factorio-bin", $"{VersionText}-{new FileInfo(Binary).Length}");
        string copy = Path.Combine(target, "factorio");
        if (!File.Exists(copy) || new FileInfo(copy).Length != new FileInfo(Binary).Length)
        {
            System.IO.Directory.CreateDirectory(target);
            foreach (string file in System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(Binary)!))
                if (Path.GetFileName(file) is "factorio" || file.EndsWith(".so", StringComparison.Ordinal))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            File.SetUnixFileMode(copy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return copy;
    }

    /// <summary>The executable bit is not enough: a folder mounted with noexec cannot run anything either.</summary>
    private static bool CanExecute(string binary)
    {
        try
        {
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(binary, "--version")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            if (probe is null)
                return false;
            probe.StandardOutput.ReadToEnd();
            probe.WaitForExit(30_000);
            return probe.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>The parts of a mod's info.json the renderer needs.</summary>
public sealed record ModInfo(string Name, string Version, IReadOnlyList<string> Dependencies)
{
    public static ModInfo? TryRead(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var dependencies = root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array
                ? deps.EnumerateArray().Select(d => d.GetString() ?? "").ToList()
                : [];
            return new ModInfo(root.GetProperty("name").GetString()!, root.GetProperty("version").GetString()!, dependencies);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Names of the mods this one cannot be loaded without. "? x" and "(?) x" are optional, "! x" excludes x;
    /// "~ x" and "+ x" are required too, they only change the load order.
    /// </summary>
    public IEnumerable<string> RequiredMods => Dependencies
        .Select(d => d.Trim())
        .Where(d => d.Length > 0 && d[0] is not ('?' or '!' or '('))
        .Select(d => d.TrimStart('~', '+', ' ').Split(' ', 2)[0]);
}
