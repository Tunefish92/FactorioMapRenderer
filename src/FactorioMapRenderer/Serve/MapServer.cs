using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FactorioMapRenderer.Game;
using FactorioMapRenderer.Save;

namespace FactorioMapRenderer.Serve;

/// <summary>
/// Serves a rendered map and lets the viewer replace the save: by upload, from a folder on this machine, or
/// over SSH; with the game server's RCON the running game is saved first. Every new save is rendered into the
/// same output directory.
/// </summary>
/// <param name="render">
/// Renders the save at the given path into <paramref name="outputDirectory"/> and reports what it is doing
/// (stage, percent of the rendering done).
/// </param>
/// <param name="bindAddress">"localhost" (default), "*" for every interface, or one address of this machine.</param>
/// <param name="adminPassword">
/// Needed to see and use the save source panel. Without one everybody who reaches the page is an admin,
/// which is only allowed while the server listens on localhost.
/// </param>
public sealed class MapServer(string outputDirectory, string stateDirectory, int port, Action<string, Action<string, int>> render, Action<string> log,
    string bindAddress = MapServer.Localhost, string? adminPassword = null)
{
    public const string Localhost = "localhost";
    private const string SessionCookie = "fmr_session";
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12), LoginLockout = TimeSpan.FromMinutes(1);

    public static bool IsLoopback(string bindAddress) =>
        bindAddress is Localhost || (IPAddress.TryParse(bindAddress, out var address) && IPAddress.IsLoopback(address));

    /// <summary>Share of the progress bar that belongs to fetching the save; the rest is rendering.</summary>
    private const int FetchShare = 20;

    private sealed record Progress(string Stage, int Percent);
    private volatile Progress _progress = new("", 0);

    private void Report(string stage, int percent)
    {
        _progress = new Progress(stage, Math.Clamp(percent, 0, 100));
        if (_job is { Running: true } job && stage != "Done")
            lock (job)
                if (job.Stages.Count == 0 || job.Stages[^1].Stage != stage)
                    job.Stages.Add((stage, DateTime.UtcNow));
    }

    /// <summary>
    /// The render that is running or ran last: at start (after an update, or of the save given on the command line)
    /// or of a save an admin loads. Every visitor's page shows it over the map, with its stages and the console's
    /// lines, and the map can only be used again once it is over: its files are being replaced meanwhile.
    /// </summary>
    private sealed class RenderJob(string reason, string title)
    {
        private static int _count;

        /// <summary>Tells a page that starts a load its own render from the one before.</summary>
        public int Id { get; } = Interlocked.Increment(ref _count);
        /// <summary>"update", "start" or "load".</summary>
        public string Reason { get; } = reason;
        public string Title { get; set; } = title;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; private set; }
        public bool Running => FinishedAt is null;
        public bool Failed { get; private set; }
        public string? Error { get; private set; }
        public List<string> Log { get; } = [];
        public List<(string Stage, DateTime At)> Stages { get; } = [];

        public void Finish(string? error = null)
        {
            lock (this)
                (Failed, Error, FinishedAt) = (error is not null, error, DateTime.UtcNow);
        }
    }
    private volatile RenderJob? _job;
    private const int RenderLogLines = 2000;

    /// <summary>Absolute paths of the server's file system; visitors see only the file's name.</summary>
    private static readonly Regex AbsolutePath = new(@"(?<![\w:/.])(?:/[^\s/]+)+/([^\s/]+)|\b[A-Za-z]:\\(?:[^\s\\]+\\)*([^\s\\]+)", RegexOptions.Compiled);

    /// <summary>A line of the console while a map is rendered: every visitor's page shows it.</summary>
    public void RenderLog(string line)
    {
        if (_job is not { Running: true } job)
            return;
        line = AbsolutePath.Replace(line, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        lock (job)
            if (job.Log.Count < RenderLogLines)
                job.Log.Add(line);
    }

    /// <summary>
    /// Renders the map at start while the pages already show how far it has come; loading another save waits
    /// until it is done. Call before <see cref="RunAsync"/>, so that the first page already knows.
    /// </summary>
    /// <param name="reason">"update": a map of an older version is brought up to date; "start": the save given at start.</param>
    public async Task RenderAtStartAsync(string reason, string saveName, Action<Action<string, int>> work, CancellationToken cancel)
    {
        var job = _job = new RenderJob(reason, saveName);
        await _busy.WaitAsync(cancel);
        try
        {
            Report("Starting", 0);
            await Task.Run(() => work(Report), cancel);
            Report("Done", 100);
            job.Finish();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RenderLog($"Could not render the map: {ex.Message}");
            job.Finish(ex.Message);
            log($"Could not render the map ({ex.Message}); load or refresh the save game in the page.");
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>How the render goes, for every page; "from" is the number of console lines the page has already.</summary>
    private object RenderState(HttpListenerRequest request)
    {
        int from = int.TryParse(request.QueryString["from"], out int f) ? Math.Max(f, 0) : 0;
        var progress = _progress;
        if (_job is not { } job)
            return new { Running = false, Finished = false, Failed = false, progress.Stage, progress.Percent, From = from, Lines = Array.Empty<string>() };
        lock (job)
        {
            var now = job.FinishedAt ?? DateTime.UtcNow;
            var stages = job.Stages.Select((s, i) => new
            {
                Name = s.Stage,
                Seconds = Math.Round(((i + 1 < job.Stages.Count ? job.Stages[i + 1].At : now) - s.At).TotalSeconds, 1),
                Done = i + 1 < job.Stages.Count || !job.Running,
            }).ToList();
            return new
            {
                job.Id, job.Reason, job.Title, job.Running, Finished = !job.Running, job.Failed,
                // what went wrong with loading a save can name the admin's hosts and folders: for admins only
                Error = job.Error is null ? null : IsAdmin(request) || job.Reason != "load" ? job.Error : "The save game could not be loaded; the map stays as it was.",
                Elapsed = Math.Round((now - job.StartedAt).TotalSeconds),
                progress.Stage, Percent = job.Running ? progress.Percent : job.Failed ? progress.Percent : 100,
                Stages = stages, From = from, Lines = job.Log.Skip(from).ToArray(),
            };
        }
    }

    private readonly ConcurrentDictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);
    private readonly object _loginLock = new(), _settingsLock = new();
    private int _failedLogins;
    private DateTime _loginsBlockedUntil;

    private const long MaxUploadBytes = 4L << 30;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".webp"] = "image/webp", [".svg"] = "image/svg+xml",
        [".ttf"] = "font/ttf", [".otf"] = "font/otf", [".txt"] = "text/plain; charset=utf-8",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.GetFullPath(outputDirectory) + Path.DirectorySeparatorChar;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private SaveSource _source = SaveSource.Load(Path.Combine(stateDirectory, "source.json"));

    private string SourceFile => Path.Combine(stateDirectory, "source.json");
    private string SavesDirectory => Path.Combine(stateDirectory, "saves");

    /// <summary>Remembers a save given on the command line as the current source.</summary>
    public void SetFileSource(string savePath)
    {
        _source = Keep(new SaveSource { Kind = SaveSource.File, Name = Path.GetFileName(savePath), LoadedAt = DateTime.UtcNow });
        _source.Save(SourceFile);
    }

    public async Task RunAsync(CancellationToken cancel)
    {
        using var listener = new HttpListener();
        if (bindAddress == Localhost)
        {
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add($"http://localhost:{port}/");
        }
        else
            listener.Prefixes.Add($"http://{(bindAddress is "*" or "0.0.0.0" or "::" ? "*" : bindAddress)}:{port}/");
        listener.Start();
        log($"Serving {outputDirectory} at http://{(bindAddress is Localhost or "*" or "0.0.0.0" or "::" ? "localhost" : bindAddress)}:{port}/ (Ctrl+C to stop)");
        log(adminPassword is null
            ? "  Reachable from this machine only; everyone who opens the page can change the save game."
            : $"  {(IsLoopback(bindAddress) ? "Reachable from this machine only" : "Reachable from other machines")}; changing the save game needs the admin password.");
        using var stop = cancel.Register(listener.Stop);
        while (!cancel.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(context, cancel), cancel);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancel)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            string path = request.Url!.AbsolutePath;
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
                await ServeFileAsync(request, response, path, cancel);
            else if (path == "/api/health" && request.HttpMethod == "GET")
                await WriteJsonAsync(response, 200, new { Status = "ok" });
            else if (path == "/api/source" && request.HttpMethod == "GET")
                // Visitors only learn how the save got here and when (shown in the status bar): no host, no path.
                await WriteJsonAsync(response, 200, IsAdmin(request) ? Describe() : new { Admin = false, LoginRequired = true, _source.Kind, _source.LoadedAt, HasServer = _source.HasRcon });
            else if (path == "/api/serverstats" && request.HttpMethod == "GET")
                await WriteJsonAsync(response, 200, await ServerStatsAsync(cancel));
            else if (path is "/api/render" or "/api/startup" && request.HttpMethod == "GET")
                // any render, for everybody: the page is blocked until it is over ("startup" for pages of 0.1.0)
                await WriteJsonAsync(response, 200, RenderState(request));
            else if (path == "/api/progress" && request.HttpMethod == "GET")
            {
                // How far the save that is being loaded has come; polled by the admin's page for its progress bar.
                var progress = _progress;
                await WriteJsonAsync(response, 200, IsAdmin(request)
                    ? new { Busy = _busy.CurrentCount == 0, progress.Stage, progress.Percent }
                    : new { Busy = false, Stage = "", Percent = 0 });
            }
            else if (request.HttpMethod != "POST")
                await WriteJsonAsync(response, 405, new { error = "Method not allowed." });
            else if (!IsSameOrigin(request))
                await WriteJsonAsync(response, 403, new { error = "Requests from other sites are not accepted." });
            else if (path == "/api/login")
                await LoginAsync(request, response, cancel);
            else if (path == "/api/logout")
            {
                if (request.Cookies[SessionCookie]?.Value is { } session)
                    _sessions.TryRemove(session, out _);
                response.Headers["Set-Cookie"] = $"{SessionCookie}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict";
                await WriteJsonAsync(response, 200, new { Admin = false });
            }
            else if (!IsAdmin(request))
                await WriteJsonAsync(response, 401, new { error = "Please log in as admin." });
            else if (path == "/api/rcon/check")
                await CheckServerAsync(request, response, cancel);
            else if (path == "/api/rcon/command")
                await RunCommandAsync(request, response, cancel);
            else if (path is "/api/rcon/settings" or "/api/ssh/settings" or "/api/folder/settings")
                await SaveSettingsAsync(request, response, path, cancel);
            else
                await ChangeSourceAsync(request, response, path, cancel);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // The browser went away; nothing to answer.
            log($"Request dropped: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"Request failed: {ex}");
            try { await WriteJsonAsync(response, 500, new { error = ex.Message }); } catch (Exception) { /* response already started */ }
        }
        finally
        {
            try { response.Close(); } catch (Exception) { /* connection already gone */ }
        }
    }

    /// <summary>
    /// Other web pages must not be able to drive this server from a visitor's browser: the custom header
    /// cannot be sent cross-origin without a preflight (which is never granted), and the Origin has to be
    /// the address the page itself was loaded from. On localhost the Host is checked against DNS rebinding.
    /// </summary>
    private bool IsSameOrigin(HttpListenerRequest request)
    {
        string? host = request.Headers["Host"];
        if (host is null || request.Headers["X-Map-Renderer"] != "1")
            return false;
        if (bindAddress == Localhost && !new[] { $"127.0.0.1:{port}", $"localhost:{port}" }.Contains(host, StringComparer.OrdinalIgnoreCase))
            return false;
        string? origin = request.Headers["Origin"];
        return origin is null || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)));
    }

    // ------------------------------------------------------------------ admin login

    private bool IsAdmin(HttpListenerRequest request)
    {
        if (adminPassword is null)
            return true;
        if (request.Cookies[SessionCookie]?.Value is not { Length: > 0 } session || !_sessions.TryGetValue(session, out var expires))
            return false;
        if (expires > DateTime.UtcNow)
            return true;
        _sessions.TryRemove(session, out _);
        return false;
    }

    private sealed record LoginRequest(string? Password);

    private async Task LoginAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancel)
    {
        if (adminPassword is null)
        {
            await WriteJsonAsync(response, 200, Describe());
            return;
        }
        bool blocked;
        lock (_loginLock)
            blocked = DateTime.UtcNow < _loginsBlockedUntil;
        if (blocked)
        {
            await WriteJsonAsync(response, 429, new { error = "Too many wrong passwords. Try again in a minute." });
            return;
        }

        LoginRequest? body = null;
        try { body = await JsonSerializer.DeserializeAsync<LoginRequest>(request.InputStream, Json, cancel); } catch (JsonException) { }
        // Compared as hashes: equal length, constant time.
        bool correct = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(body?.Password ?? "")), SHA256.HashData(Encoding.UTF8.GetBytes(adminPassword)));
        if (!correct)
        {
            lock (_loginLock)
                if (++_failedLogins >= MaxFailedLogins)
                {
                    _failedLogins = 0;
                    _loginsBlockedUntil = DateTime.UtcNow + LoginLockout;
                    log($"Admin login blocked for {LoginLockout.TotalSeconds:0} s after {MaxFailedLogins} wrong passwords (last from {request.RemoteEndPoint?.Address}).");
                }
            await Task.Delay(500, cancel);
            await WriteJsonAsync(response, 401, new { error = "Wrong password." });
            return;
        }

        lock (_loginLock)
            _failedLogins = 0;
        foreach (var (key, expires) in _sessions)
            if (expires <= DateTime.UtcNow)
                _sessions.TryRemove(key, out _);
        string session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[session] = DateTime.UtcNow + SessionLifetime;
        // Secure when a TLS-terminating proxy is in front; the listener itself only speaks HTTP.
        bool https = string.Equals(request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
        response.Headers["Set-Cookie"] = $"{SessionCookie}={session}; Path=/; Max-Age={(int)SessionLifetime.TotalSeconds}; HttpOnly; SameSite=Strict{(https ? "; Secure" : "")}";
        log($"Admin logged in from {request.RemoteEndPoint?.Address}.");
        await WriteJsonAsync(response, 200, Describe());
    }

    // ------------------------------------------------------------------ static files

    /// <summary>Text files go out compressed; images are compressed already.</summary>
    private static readonly HashSet<string> Compressible = new(StringComparer.OrdinalIgnoreCase) { ".html", ".js", ".css", ".json", ".svg", ".txt" };

    /// <summary>Compressed once per file and render; small next to the map (a tenth of its text files).</summary>
    private sealed record Compressed(DateTime Modified, long Length, byte[] Bytes);
    private readonly ConcurrentDictionary<string, Compressed> _compressed = new();
    private long _compressedBytes;
    private const long MaxCompressedBytes = 256L << 20;

    private async Task ServeFileAsync(HttpListenerRequest request, HttpListenerResponse response, string urlPath, CancellationToken cancel)
    {
        string relative = Uri.UnescapeDataString(urlPath).TrimStart('/');
        string file = Path.GetFullPath(Path.Combine(_root, relative.Length == 0 ? "index.html" : relative));
        var info = new FileInfo(file);
        if (!file.StartsWith(_root, StringComparison.Ordinal) || !info.Exists)
        {
            response.StatusCode = 404;
            return;
        }

        // A new render replaces files under the same names. The viewer asks for the files of a render with its
        // build (?v=...), and those the browser may keep; everything else it has to check with every load.
        var modified = info.LastWriteTimeUtc;
        response.Headers["Cache-Control"] = request.QueryString["v"] is { Length: > 0 } ? "public, max-age=31536000, immutable" : "no-cache";
        response.Headers["Last-Modified"] = modified.ToString("R");
        if (DateTime.TryParse(request.Headers["If-Modified-Since"], null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var since)
            && modified <= since.AddSeconds(1))
        {
            response.StatusCode = 304;
            return;
        }
        string extension = Path.GetExtension(file);
        response.ContentType = ContentTypes.GetValueOrDefault(extension, "application/octet-stream");
        if (Compressible.Contains(extension))
        {
            response.Headers["Vary"] = "Accept-Encoding";
            if (request.Headers["Accept-Encoding"]?.Contains("gzip", StringComparison.OrdinalIgnoreCase) == true)
            {
                byte[] body = await CompressedAsync(file, info, cancel);
                response.Headers["Content-Encoding"] = "gzip";
                response.ContentLength64 = body.Length;
                if (request.HttpMethod != "HEAD")
                    await response.OutputStream.WriteAsync(body, cancel);
                return;
            }
        }
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        response.ContentLength64 = stream.Length;
        if (request.HttpMethod != "HEAD")
            await stream.CopyToAsync(response.OutputStream, cancel);
    }

    private async Task<byte[]> CompressedAsync(string file, FileInfo info, CancellationToken cancel)
    {
        if (_compressed.TryGetValue(file, out var cached) && cached.Modified == info.LastWriteTimeUtc && cached.Length == info.Length)
            return cached.Bytes;
        var buffer = new MemoryStream();
        await using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true))
        await using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            await source.CopyToAsync(gzip, cancel);
        var entry = new Compressed(info.LastWriteTimeUtc, info.Length, buffer.ToArray());
        // Files of an older render are left behind under names nobody asks for any more: start afresh then.
        if (Interlocked.Add(ref _compressedBytes, entry.Bytes.Length) > MaxCompressedBytes)
        {
            _compressed.Clear();
            Interlocked.Exchange(ref _compressedBytes, entry.Bytes.Length);
        }
        _compressed[file] = entry;
        return entry.Bytes;
    }

    // ------------------------------------------------------------------ the game server (RCON)

    private static readonly TimeSpan StatsTimeout = TimeSpan.FromSeconds(8), StatsAge = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _statsLock = new(1, 1);
    private object? _stats;
    private DateTime _statsAt;

    /// <summary>
    /// What the game server says about itself (version, players, map age, evolution), for the page's Server tab.
    /// Every visitor may see it, so the answer is kept for a few seconds: many open pages are one question to
    /// the game server, not many.
    /// </summary>
    private async Task<object> ServerStatsAsync(CancellationToken cancel)
    {
        var source = _source;
        if (!source.HasRcon)
            return new { Available = false };
        await _statsLock.WaitAsync(cancel);
        try
        {
            if (_stats is not null && DateTime.UtcNow - _statsAt < StatsAge)
                return _stats;
            try
            {
                await using var rcon = await RconClient.ConnectAsync(source.RconHost, source.RconPort, source.RconPassword, StatsTimeout, cancel);
                string version = await rcon.RunAsync("/version", StatsTimeout, cancel);
                string players = await rcon.RunAsync("/players online", StatsTimeout, cancel);
                string time = await rcon.RunAsync("/time", StatsTimeout, cancel);
                string evolution = await rcon.RunAsync("/evolution", StatsTimeout, cancel);
                _stats = new
                {
                    Available = true, At = DateTime.UtcNow, Version = version.Trim(), Time = time.Trim(), Evolution = evolution.Trim(),
                    Players = ParsePlayers(players),
                };
            }
            catch (SaveSourceException ex)
            {
                // e.g. the server is down or restarting: that is a state worth showing, not a failure
                _stats = new { Available = true, At = DateTime.UtcNow, Error = ex.Message };
            }
            _statsAt = DateTime.UtcNow;
            return _stats;
        }
        finally
        {
            _statsLock.Release();
        }
    }

    /// <summary>"Online players (2):\n  alice (online)\n  bob (online)" → [alice, bob]</summary>
    internal static string[] ParsePlayers(string answer) => answer
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(l => !l.EndsWith("):", StringComparison.Ordinal) && !l.StartsWith("Online players", StringComparison.Ordinal))
        .Select(l => Regex.Replace(l, @"\s*\((online|offline)\)$", ""))
        .ToArray();

    private sealed record CommandRequest(string? Command);

    /// <summary>Admin only: runs a console command on the game server.</summary>
    private async Task RunCommandAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancel)
    {
        var source = _source;
        if (!source.HasRcon)
        {
            await WriteJsonAsync(response, 400, new { error = "No game server (RCON) is set up." });
            return;
        }
        CommandRequest? body = null;
        try { body = await JsonSerializer.DeserializeAsync<CommandRequest>(request.InputStream, Json, cancel); } catch (JsonException) { }
        string command = body?.Command?.Trim() ?? "";
        if (command.Length == 0 || command.Length > 2000)
        {
            await WriteJsonAsync(response, 400, new { error = "Enter a console command." });
            return;
        }
        try
        {
            log($"Admin runs on the game server: {command}");
            await using var rcon = await RconClient.ConnectAsync(source.RconHost, source.RconPort, source.RconPassword, StatsTimeout, cancel);
            string output = await rcon.RunAsync(command, TimeSpan.FromSeconds(30), cancel);
            _stats = null;
            await WriteJsonAsync(response, 200, new { Output = output });
        }
        catch (SaveSourceException ex)
        {
            await WriteJsonAsync(response, 502, new { error = ex.Message });
        }
    }

    private object Describe() => new
    {
        Admin = true, LoginRequired = adminPassword is not null,
        _source.Kind, _source.Name, _source.LoadedAt,
        _source.FolderPath, _source.SshTarget, _source.SshPort, _source.SshPath,
        _source.RconHost, _source.RconPort, HasPassword = _source.RconPassword.Length > 0, _source.SaveFirst, _source.ServerInfo,
        HasServer = _source.HasRcon,
        Busy = _busy.CurrentCount == 0,
    };

    private sealed record FolderRequest(string? Path);
    private sealed record SshRequest(string? Target, int? Port, string? Path);
    private sealed record RconRequest(string? Host, int? Port, string? Password, bool? SaveFirst);

    private async Task ChangeSourceAsync(HttpListenerRequest request, HttpListenerResponse response, string path, CancellationToken cancel)
    {
        if (!await _busy.WaitAsync(0, cancel))
        {
            await WriteJsonAsync(response, 409, new { error = "Another save is being loaded right now." });
            return;
        }
        var job = _job = new RenderJob("load", "A new save game");
        try
        {
            Directory.CreateDirectory(SavesDirectory);
            Report("Starting", 0);
            SaveSource source;
            string saveFile;
            switch (path)
            {
                case "/api/upload":
                {
                    if (request.ContentLength64 > MaxUploadBytes)
                        throw new SaveSourceException("The file is too large to be a save game.");
                    string name = Path.GetFileName(request.QueryString["name"] ?? "");
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        throw new SaveSourceException("Please choose a Factorio save file (.zip).");
                    saveFile = Path.Combine(SavesDirectory, name);
                    Report("Receiving the file", 10);
                    await using (var file = File.Create(saveFile))
                        await request.InputStream.CopyToAsync(file, cancel);
                    // the browser tells when the file was last written: the time of the save
                    if (long.TryParse(request.QueryString["modified"], out long modified) && modified > 0)
                        File.SetLastWriteTimeUtc(saveFile, DateTime.UnixEpoch.AddMilliseconds(modified));
                    source = Keep(new SaveSource { Kind = SaveSource.File, Name = name });
                    break;
                }
                case "/api/folder":
                {
                    var body = await JsonSerializer.DeserializeAsync<FolderRequest>(request.InputStream, Json, cancel) ?? throw new SaveSourceException("Missing folder.");
                    source = FromRequest(body);
                    Remember(source);
                    (saveFile, source.Name) = await FetchAsync(source, cancel);
                    break;
                }
                case "/api/ssh":
                {
                    var body = await JsonSerializer.DeserializeAsync<SshRequest>(request.InputStream, Json, cancel) ?? throw new SaveSourceException("Missing SSH settings.");
                    source = FromRequest(body);
                    Remember(source);
                    (saveFile, source.Name) = await FetchAsync(source, cancel);
                    break;
                }
                case "/api/refresh":
                    if (_source.Kind is not (SaveSource.Folder or SaveSource.Ssh))
                        throw new SaveSourceException("Only a save from a folder or over SSH can be refreshed.");
                    source = Keep(new SaveSource { Kind = _source.Kind });
                    (saveFile, source.Name) = await FetchAsync(source, cancel);
                    break;
                default:
                    await WriteJsonAsync(response, 404, new { error = "Unknown function." });
                    return;
            }

            job.Title = Path.GetFileName(saveFile);
            await Task.Run(() => render(saveFile, (stage, percent) => Report(stage, FetchShare + percent * (100 - FetchShare) / 100)), cancel);
            Report("Done", 100);
            job.Finish();
            source.LoadedAt = DateTime.UtcNow;
            lock (_settingsLock)
            {
                // Settings entered while this save was loading win.
                _source = Keep(source);
                _source.Save(SourceFile);
            }
            await WriteJsonAsync(response, 200, Describe());
        }
        catch (Exception ex) when (ex is SaveSourceException or SaveFormatException or FactorioRunException or InvalidOperationException or JsonException)
        {
            string message = ex is SaveFormatException ? $"This is not a readable save game: {ex.Message}" : ex is JsonException ? "Invalid request." : ex.Message;
            log($"Could not load the save: {message}");
            job.Finish(message);
            await WriteJsonAsync(response, 400, new { error = message });
        }
        catch (Exception ex)
        {
            job.Finish(ex.Message);
            throw;
        }
        finally
        {
            _busy.Release();
        }
    }

    private SaveSource FromRequest(FolderRequest body)
    {
        var source = Keep(new SaveSource { Kind = SaveSource.Folder });
        source.FolderPath = (body.Path ?? "").Trim();
        return source;
    }

    private SaveSource FromRequest(SshRequest body)
    {
        var source = Keep(new SaveSource { Kind = SaveSource.Ssh });
        source.SshTarget = (body.Target ?? "").Trim();
        source.SshPort = body.Port ?? 22;
        source.SshPath = (body.Path ?? "").Trim();
        return source;
    }

    private SaveSource FromRequest(RconRequest body)
    {
        var source = Keep(new SaveSource { Kind = _source.Kind });
        string host = (body.Host ?? "").Trim();
        // An empty password field means "keep the stored one", but never for a different server.
        source.RconPassword = !string.IsNullOrEmpty(body.Password) ? body.Password
            : string.Equals(host, _source.RconHost, StringComparison.OrdinalIgnoreCase) && (body.Port ?? 27015) == _source.RconPort ? _source.RconPassword : "";
        source.RconHost = host;
        source.RconPort = body.Port ?? 27015;
        source.SaveFirst = body.SaveFirst ?? true;
        return source;
    }

    /// <summary>
    /// Connection settings are stored as soon as they are entered (saved, checked or used), whether or not
    /// the connection works, so they never have to be typed twice. The save that is shown stays the same.
    /// </summary>
    private void Remember(SaveSource entered, bool rcon = false)
    {
        lock (_settingsLock)
        {
            if (rcon)
            {
                (_source.RconHost, _source.RconPort, _source.RconPassword, _source.SaveFirst) = (entered.RconHost, entered.RconPort, entered.RconPassword, entered.SaveFirst);
                _stats = null;
            }
            else if (entered.Kind == SaveSource.Folder)
                _source.FolderPath = entered.FolderPath;
            else
                (_source.SshTarget, _source.SshPort, _source.SshPath) = (entered.SshTarget, entered.SshPort, entered.SshPath);
            _source.Save(SourceFile);
        }
    }

    private async Task SaveSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, string path, CancellationToken cancel)
    {
        try
        {
            if (path == "/api/rcon/settings")
                Remember(FromRequest(await JsonSerializer.DeserializeAsync<RconRequest>(request.InputStream, Json, cancel) ?? throw new JsonException()), rcon: true);
            else if (path == "/api/ssh/settings")
                Remember(FromRequest(await JsonSerializer.DeserializeAsync<SshRequest>(request.InputStream, Json, cancel) ?? throw new JsonException()));
            else
                Remember(FromRequest(await JsonSerializer.DeserializeAsync<FolderRequest>(request.InputStream, Json, cancel) ?? throw new JsonException()));
            await WriteJsonAsync(response, 200, Describe());
        }
        catch (JsonException)
        {
            await WriteJsonAsync(response, 400, new { error = "Invalid request." });
        }
    }

    /// <summary>Logs in to the game server's RCON and reports its version, without loading anything.</summary>
    private async Task CheckServerAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancel)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<RconRequest>(request.InputStream, Json, cancel) ?? throw new SaveSourceException("Missing server settings.");
            var entered = FromRequest(body);
            Remember(entered, rcon: true);
            await using var rcon = await RconClient.ConnectAsync(entered.RconHost, entered.RconPort, entered.RconPassword, StatsTimeout, cancel);
            string version = (await rcon.RunAsync("/version", StatsTimeout, cancel)).Trim();
            string players = await rcon.RunAsync("/players online", StatsTimeout, cancel);
            lock (_settingsLock)
            {
                _source.ServerInfo = $"Factorio {version}";
                _source.Save(SourceFile);
            }
            await WriteJsonAsync(response, 200, new { Version = version, Online = ParsePlayers(players).Length });
        }
        catch (Exception ex) when (ex is SaveSourceException or JsonException)
        {
            await WriteJsonAsync(response, 400, new { error = ex is JsonException ? "Invalid request." : ex.Message });
        }
    }

    /// <summary>A new source keeps the connection settings of the other kinds, so their forms stay filled in.</summary>
    private SaveSource Keep(SaveSource next)
    {
        next.FolderPath = _source.FolderPath;
        (next.SshTarget, next.SshPort, next.SshPath) = (_source.SshTarget, _source.SshPort, _source.SshPath);
        (next.RconHost, next.RconPort, next.RconPassword, next.SaveFirst, next.ServerInfo) =
            (_source.RconHost, _source.RconPort, _source.RconPassword, _source.SaveFirst, _source.ServerInfo);
        return next;
    }

    private async Task<(string File, string Name)> FetchAsync(SaveSource source, CancellationToken cancel)
    {
        string file = Path.Combine(SavesDirectory, source.Kind == SaveSource.Ssh ? "ssh-snapshot.zip" : "folder-snapshot.zip");
        string name = await SaveFetcher.FetchAsync(source, file, log, Report, cancel);
        return (file, source.Kind == SaveSource.Ssh ? $"{name} from {source.SshTarget}" : name);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, object body)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.Headers["Cache-Control"] = "no-store";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
