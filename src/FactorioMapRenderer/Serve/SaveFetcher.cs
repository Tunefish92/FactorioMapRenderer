using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FactorioMapRenderer.Serve;

/// <summary>
/// Gets the newest save from a folder on this machine or over SSH. With an RCON connection the running game is
/// saved first, and the newest save is taken once the server has written it.
/// </summary>
public static partial class SaveFetcher
{
    private static readonly TimeSpan SaveWait = TimeSpan.FromSeconds(90);

    /// <summary>Copies the newest save into <paramref name="targetFile"/>; returns its name.</summary>
    public static async Task<string> FetchAsync(SaveSource source, string targetFile, Action<string> log, Action<string, int> progress, CancellationToken cancel)
    {
        Validate(source);
        DateTime? before = null;
        if (source.HasRcon && source.SaveFirst)
        {
            before = (await NewestAsync(source, cancel))?.Modified;
            progress("Saving the running game", 4);
            log($"Saving the running game on {source.RconHost} over RCON");
            await using var rcon = await RconClient.ConnectAsync(source.RconHost, source.RconPort, source.RconPassword, TimeSpan.FromSeconds(10), cancel);
            string answer = await rcon.RunAsync("/server-save", TimeSpan.FromSeconds(30), cancel);
            if (answer.Length > 0)
                log($"  server: {answer}");
        }

        // The server writes the save after it answered; it is complete once it is newer and stays the same
        // between two looks. Without saving first the newest save is taken at once.
        var watch = Stopwatch.StartNew();
        (string Path, DateTime Modified, long Size)? previous = null, save;
        while (true)
        {
            var found = await NewestAsync(source, cancel);
            bool fresh = found is not null && (before is null || found.Value.Modified > before);
            if (fresh && (before is null || Equals(previous, found)))
            {
                save = found;
                break;
            }
            if (watch.Elapsed > SaveWait)
            {
                if (found is null)
                    throw new SaveSourceException("No .zip save found there.");
                log("  The server wrote no new save in time; taking the newest one there is.");
                save = found;
                break;
            }
            if (found is null && before is null)
                throw new SaveSourceException("No .zip save found there.");
            previous = fresh ? found : null;
            progress("Waiting for the server to write the save", 6);
            await Task.Delay(TimeSpan.FromSeconds(2), cancel);
        }

        var (path, modified, _) = save!.Value;
        progress(source.Kind == SaveSource.Ssh ? "Copying the save game over SSH" : "Copying the save game", 12);
        log($"Copying {path}");
        if (source.Kind == SaveSource.Folder)
            File.Copy(path, targetFile, overwrite: true);
        else
            await SshAsync(source, $"cat -- {Quote(path)}", targetFile, cancel);
        File.SetLastWriteTimeUtc(targetFile, modified);
        return Path.GetFileName(path);
    }

    private static void Validate(SaveSource source)
    {
        if (source.Kind == SaveSource.Folder)
        {
            if (source.FolderPath.Length == 0)
                throw new SaveSourceException("Enter the folder with the saves, or a save file.");
            if (!Directory.Exists(source.FolderPath) && !File.Exists(source.FolderPath))
                throw new SaveSourceException($"{source.FolderPath} does not exist here.");
            return;
        }
        if (!TargetPattern().IsMatch(source.SshTarget.Trim()))
            throw new SaveSourceException("Enter the SSH target as user@host (or a host from your ssh config).");
        if (source.SshPath.Trim().Length == 0 || source.SshPath.Contains('\n') || source.SshPath.Contains('\0'))
            throw new SaveSourceException("Enter the path of a save (.zip) or of the folder with the saves on the remote machine.");
        if (source.SshPort is < 1 or > 65535)
            throw new SaveSourceException("The SSH port must be between 1 and 65535.");
    }

    /// <summary>The newest .zip in the folder (or the file itself), with its time and size; null if there is none.
    /// Factorio writes a save as name.tmp.zip first and renames it when it is complete; those are left out, as one
    /// is unfinished or left over from a save that was cut off.</summary>
    private static async Task<(string Path, DateTime Modified, long Size)?> NewestAsync(SaveSource source, CancellationToken cancel)
    {
        if (source.Kind == SaveSource.Folder)
        {
            var file = File.Exists(source.FolderPath)
                ? new FileInfo(source.FolderPath)
                : new DirectoryInfo(source.FolderPath).EnumerateFiles("*.zip", SearchOption.AllDirectories)
                    .Where(f => !f.Name.EndsWith(".tmp.zip", StringComparison.OrdinalIgnoreCase))
                    .MaxBy(f => f.LastWriteTimeUtc);
            return file is null ? null : (file.FullName, file.LastWriteTimeUtc, file.Length);
        }
        string path = source.SshPath.Trim();
        string quoted = path.StartsWith("~/", StringComparison.Ordinal) ? "~/" + Quote(path[2..]) : Quote(path);
        string listing = await SshAsync(source,
            $"find {quoted} -type f -name '*.zip' ! -name '*.tmp.zip' -printf '%T@ %s %p\\n' 2>/dev/null | sort -rn | head -n 1", null, cancel);
        var parts = listing.Trim().Split(' ', 3);
        if (parts.Length < 3)
            return null;
        var modified = DateTime.UnixEpoch.AddSeconds(double.Parse(parts[0], CultureInfo.InvariantCulture));
        return (parts[2], modified, long.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Runs a command over SSH: its output goes into a file, or is returned.</summary>
    private static async Task<string> SshAsync(SaveSource source, string command, string? outputFile, CancellationToken cancel)
    {
        var start = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (string arg in new[] { "-p", source.SshPort.ToString(CultureInfo.InvariantCulture), "-o", "BatchMode=yes", "-o", "ConnectTimeout=15",
                     // A host seen for the first time is remembered without asking (there is nobody to ask); a changed key is refused.
                     "-o", "StrictHostKeyChecking=accept-new", "-T", "--", source.SshTarget.Trim(), command })
            start.ArgumentList.Add(arg);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new SaveSourceException("Could not start ssh.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new SaveSourceException("The ssh program was not found on this machine.");
        }
        using (process)
        {
            process.StandardInput.Close();
            var errors = process.StandardError.ReadToEndAsync(cancel);
            string output = "";
            if (outputFile is null)
                output = await process.StandardOutput.ReadToEndAsync(cancel);
            else
            {
                await using var file = File.Create(outputFile);
                await process.StandardOutput.BaseStream.CopyToAsync(file, cancel);
            }
            await process.WaitForExitAsync(cancel);
            string error = (await errors).Trim();
            if (process.ExitCode != 0)
                throw new SaveSourceException(error.Length > 0 ? $"ssh: {error.Split('\n').Last().Trim()}" : $"ssh ended with exit code {process.ExitCode}.");
            return output;
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>No leading dash, so the target can never be read as an ssh option.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.@\-]*$")]
    private static partial Regex TargetPattern();
}
