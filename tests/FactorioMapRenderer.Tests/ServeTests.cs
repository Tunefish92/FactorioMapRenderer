using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FactorioMapRenderer.Game;
using FactorioMapRenderer.Serve;
using FactorioMapRenderer.Tests.TestSupport;

namespace FactorioMapRenderer.Tests;

public sealed class RconTests
{
    [Fact]
    public void Packets_round_trip()
    {
        byte[] packet = RconClient.Encode(7, 2, "/players online");
        Assert.Equal(packet.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(packet));
        var (id, type, body) = RconClient.Decode(packet.AsSpan(4));
        Assert.Equal((7, 2, "/players online"), (id, type, body));
    }

    [Fact]
    public async Task Logs_in_and_runs_a_command()
    {
        using var server = new FakeRcon("secret");
        await using var rcon = await RconClient.ConnectAsync("127.0.0.1", server.Port, "secret", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("ran: /version", await rcon.RunAsync("/version", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_wrong_password_is_reported()
    {
        using var server = new FakeRcon("secret");
        var error = await Assert.ThrowsAsync<SaveSourceException>(() =>
            RconClient.ConnectAsync("127.0.0.1", server.Port, "wrong", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Contains("refused", error.Message);
    }

    [Fact]
    public void Lists_the_players_online()
    {
        Assert.Equal(["alice", "bob"], MapServer.ParsePlayers("Online players (2):\n  alice (online)\n  bob (online)\n"));
        Assert.Empty(MapServer.ParsePlayers("Online players (0):"));
    }

    /// <summary>Answers like Factorio: an auth response (id -1 on a wrong password), then one answer per command.</summary>
    private sealed class FakeRcon : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeRcon(string password)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                while (true)
                {
                    var head = new byte[4];
                    if (await stream.ReadAtLeastAsync(head, 4, throwOnEndOfStream: false) < 4)
                        return;
                    var rest = new byte[BinaryPrimitives.ReadInt32LittleEndian(head)];
                    await stream.ReadExactlyAsync(rest);
                    var (id, type, body) = RconClient.Decode(rest);
                    byte[] answer = type == 3
                        ? RconClient.Encode(body == password ? id : -1, 2, "")
                        : RconClient.Encode(id, 0, "ran: " + body);
                    await stream.WriteAsync(answer);
                }
            });
        }

        public void Dispose() => _listener.Stop();
    }
}

public sealed class GameTests
{
    [Fact]
    public void Required_dependencies_of_a_mod()
    {
        var mod = new ModInfo("space-age", "2.1.23", ["base >= 2.1.0", "elevated-rails >= 2.1.0", "+ quality >= 2.1.0", "~ recycler", "? optional-mod", "! enemy-mod", "(?) hidden"]);
        Assert.Equal(["base", "elevated-rails", "quality", "recycler"], mod.RequiredMods);
    }

    [Fact]
    public void Colours_in_either_range()
    {
        Assert.Equal(new SkiaSharp.SKColor(255, 128, 0), GameData.ToColor(System.Text.Json.Nodes.JsonNode.Parse("[1, 0.5, 0]")!));
        Assert.Equal(new SkiaSharp.SKColor(202, 167, 24), GameData.ToColor(System.Text.Json.Nodes.JsonNode.Parse("[202, 167, 24]")!));
        Assert.Equal(new SkiaSharp.SKColor(0, 0, 255, 128), GameData.ToColor(System.Text.Json.Nodes.JsonNode.Parse("""{"b": 1, "a": 0.5}""")!));
    }

    [Fact]
    public void Ids_become_readable_names()
    {
        Assert.Equal("Assembling machine 2", GameData.Readable("assembling-machine-2"));
        Assert.Equal("Iron gear wheel", GameData.Empty.Name("recipe", "iron-gear-wheel"));
    }

    [Fact]
    public void English_names_come_from_the_locale_files()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"fmr-locale-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "base", "locale", "en"));
            Directory.CreateDirectory(Path.Combine(dir, "base", "locale", "de"));
            File.WriteAllText(Path.Combine(dir, "base", "locale", "en", "base.cfg"), "[item-name]\niron-plate=Iron plate\n; comment\n[entity-name]\nfurnace=Furnace\n");
            File.WriteAllText(Path.Combine(dir, "base", "locale", "de", "base.cfg"), "[item-name]\niron-plate=Eisenplatte\n");
            var locale = GameData.ReadLocale(dir);
            Assert.Equal("Iron plate", locale["item-name.iron-plate"]);
            Assert.Equal("Furnace", locale["entity-name.furnace"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class StaticFileTests
{
    [Fact]
    public async Task Files_of_a_render_are_kept_by_the_browser_and_text_goes_out_compressed()
    {
        string root = TestFiles.TempDirectory();
        int port = FreePort();
        var cancel = TestContext.Current.CancellationToken;
        using var stop = new CancellationTokenSource();
        try
        {
            string text = string.Concat(Enumerable.Repeat("FMR_REGION(1, \"0_0\", {});\n", 2000));
            Directory.CreateDirectory(Path.Combine(root, "map", "surfaces"));
            File.WriteAllText(Path.Combine(root, "map", "surfaces", "1.js"), text);
            File.WriteAllText(Path.Combine(root, "map", "index.html"), "<title>map</title>");
            var server = new MapServer(Path.Combine(root, "map"), Path.Combine(root, "state"), port, (_, _, _) => { }, _ => { });
            var running = server.RunAsync(stop.Token);

            using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None });
            var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/surfaces/1.js?v=abc");
            request.Headers.AcceptEncoding.ParseAdd("gzip");
            using var versioned = await http.SendAsync(request, cancel);
            Assert.Equal(HttpStatusCode.OK, versioned.StatusCode);
            Assert.Equal("public, max-age=31536000, immutable", versioned.Headers.CacheControl?.ToString());
            Assert.Equal("gzip", Assert.Single(versioned.Content.Headers.ContentEncoding));
            byte[] body = await versioned.Content.ReadAsByteArrayAsync(cancel);
            Assert.True(body.Length < text.Length / 10, $"{body.Length} bytes");
            using (var gzip = new System.IO.Compression.GZipStream(new MemoryStream(body), System.IO.Compression.CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
                Assert.Equal(text, await reader.ReadToEndAsync(cancel));

            // the page itself and anything asked for without a build: the browser checks it with every load
            using var page = await http.GetAsync($"http://localhost:{port}/", cancel);
            Assert.Equal("no-cache", page.Headers.CacheControl?.ToString());
            Assert.Empty(page.Content.Headers.ContentEncoding);
            Assert.Equal("<title>map</title>", await page.Content.ReadAsStringAsync(cancel));

            stop.Cancel();
            await running;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
