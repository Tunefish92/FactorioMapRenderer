using System.Diagnostics;
using System.Text.Json;

namespace FactorioMapRenderer.Game;

/// <summary>
/// Fetches Factorio's headless server from factorio.com when no game is at hand. It is free to download, needs no
/// account, and is all the renderer needs to read a save; the icons then come from the bundled game data.
/// </summary>
public static class HeadlessDownloader
{
    private const string Releases = "https://factorio.com/api/latest-releases";
    private const string Download = "https://factorio.com/get-download/{0}/headless/linux64";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    /// <summary>
    /// A headless server that can load a save of <paramref name="saveVersion"/>: the newest stable release, or the
    /// save's own version when the save is newer than that (an experimental release). Downloaded once per version.
    /// </summary>
    public static async Task<FactorioInstallation> EnsureAsync(string cacheDirectory, Version saveVersion, Action<string> log,
        Action<string, int>? progress, CancellationToken cancel = default)
    {
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new InvalidOperationException("Factorio's headless server only exists for x86-64; mount a game for this machine at /game instead.");

        string root = Path.Combine(cacheDirectory, "factorio");
        string version = await ChooseVersionAsync(saveVersion, root, log, cancel);
        string target = Path.Combine(root, version);
        if (FactorioInstallation.TryOpen(target) is { Binary: not null } cached)
            return cached;

        Directory.CreateDirectory(root);
        string archive = Path.Combine(root, $"factorio-headless_{version}.tar.xz");
        log($"Downloading Factorio's headless server {version} from factorio.com (once; about 60 MB)");
        progress?.Invoke($"Downloading Factorio {version} (headless server)", 4);
        using (var response = await Http.GetAsync(string.Format(Download, version), HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"factorio.com answered the download of {version} with HTTP {(int)response.StatusCode}.");
            await using var file = File.Create(archive);
            await response.Content.CopyToAsync(file, cancel);
        }

        string unpacking = target + ".partial";
        if (Directory.Exists(unpacking))
            Directory.Delete(unpacking, recursive: true);
        Directory.CreateDirectory(unpacking);
        var tar = new ProcessStartInfo("tar") { RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { "-xJf", archive, "-C", unpacking })
            tar.ArgumentList.Add(arg);
        using (var process = Process.Start(tar) ?? throw new InvalidOperationException("Could not start tar."))
        {
            string error = await process.StandardError.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The download could not be unpacked: {error.Trim()}");
        }
        File.Delete(archive);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(unpacking, target);
        return FactorioInstallation.TryOpen(target) is { Binary: not null } installed
            ? installed
            : throw new InvalidOperationException("The downloaded archive holds no Factorio server.");
    }

    private static async Task<string> ChooseVersionAsync(Version saveVersion, string root, Action<string> log, CancellationToken cancel)
    {
        string saveText = $"{saveVersion.Major}.{saveVersion.Minor}.{saveVersion.Build}";
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(Releases, cancel));
            string stable = doc.RootElement.GetProperty("stable").GetProperty("headless").GetString()!;
            return Version.Parse(stable) >= new Version(saveVersion.Major, saveVersion.Minor, saveVersion.Build) ? stable : saveText;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            // Offline: whatever was downloaded before and is new enough.
            var known = Directory.Exists(root)
                ? Directory.EnumerateDirectories(root).Select(Path.GetFileName)
                    .Where(n => Version.TryParse(n, out var v) && v >= new Version(saveVersion.Major, saveVersion.Minor, saveVersion.Build))
                    .OrderByDescending(n => Version.Parse(n!)).FirstOrDefault()
                : null;
            if (known is not null)
                return known;
            log($"  factorio.com could not be asked for the newest version ({ex.Message}); trying {saveText}.");
            return saveText;
        }
    }
}
