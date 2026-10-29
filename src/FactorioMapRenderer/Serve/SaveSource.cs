using System.Text.Json;

namespace FactorioMapRenderer.Serve;

/// <summary>Where the save shown by the served map comes from. Persisted so "refresh" survives a restart.</summary>
public sealed class SaveSource
{
    public const string File = "file", Folder = "folder", Ssh = "ssh";

    public string Kind { get; set; } = "";

    /// <summary>Name shown in the viewer: the file name, or the folder or host and remote path.</summary>
    public string Name { get; set; } = "";
    public DateTime? LoadedAt { get; set; }

    // A folder on this machine (in Docker: a mount), e.g. the saves of a server running next to it
    public string FolderPath { get; set; } = "";

    // SSH
    public string SshTarget { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshPath { get; set; } = "";

    // The game server's RCON: saves the running game before it is fetched, and runs the Server tab
    public string RconHost { get; set; } = "";
    public int RconPort { get; set; } = 27015;
    public string RconPassword { get; set; } = "";
    public bool SaveFirst { get; set; } = true;

    /// <summary>What the server said about itself last ("Factorio 2.0.32").</summary>
    public string ServerInfo { get; set; } = "";

    public bool HasRcon => RconHost.Length > 0 && RconPassword.Length > 0;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static SaveSource Load(string path)
    {
        try
        {
            return System.IO.File.Exists(path) ? JsonSerializer.Deserialize<SaveSource>(System.IO.File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    /// <summary>The file holds the RCON password, so it is readable by the current user only.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>A problem the user can fix (wrong password, unreachable host...); its message is shown in the viewer.</summary>
public sealed class SaveSourceException(string message) : Exception(message);
