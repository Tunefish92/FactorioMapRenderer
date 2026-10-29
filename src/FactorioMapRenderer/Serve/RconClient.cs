using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace FactorioMapRenderer.Serve;

/// <summary>
/// The Source RCON protocol, which Factorio's dedicated server speaks when started with --rcon-port and
/// --rcon-password. A packet is: size (i32, of what follows), id (i32), type (i32), body, two zero bytes.
/// Types: 3 log in, 2 run a command (and the answer to logging in), 0 the answer to a command.
/// </summary>
public sealed class RconClient : IAsyncDisposable
{
    private const int Login = 3, Command = 2, Answer = 0, LoginAnswer = 2;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private int _id;

    private RconClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<RconClient> ConnectAsync(string host, int port, string password, TimeSpan timeout, CancellationToken cancel)
    {
        if (host.Length == 0 || password.Length == 0)
            throw new SaveSourceException("Enter the server's address and its RCON password.");
        if (port is < 1 or > 65535)
            throw new SaveSourceException("The RCON port must be between 1 and 65535.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, limit.Token);
            var rcon = new RconClient(client);
            int id = await rcon.SendAsync(Login, password, limit.Token);
            while (true)
            {
                var (answerId, type, _) = await rcon.ReadAsync(limit.Token);
                if (type != LoginAnswer)
                    continue;
                if (answerId == -1)
                    throw new SaveSourceException("The server refused the RCON password.");
                if (answerId == id)
                    return rcon;
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            client.Dispose();
            throw new SaveSourceException(ex is OperationCanceledException
                ? $"{host}:{port} did not answer within {timeout.TotalSeconds:0} seconds."
                : $"{host}:{port} cannot be reached: {ex.Message}");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Runs a console command ("/version", "/players online", "/server-save") and returns what it printed.</summary>
    public async Task<string> RunAsync(string command, TimeSpan timeout, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        try
        {
            int id = await SendAsync(Command, command, limit.Token);
            while (true)
            {
                var (answerId, type, body) = await ReadAsync(limit.Token);
                if (answerId == id && type == Answer)
                    return body.TrimEnd('\n', '\r');
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            throw new SaveSourceException(ex is OperationCanceledException ? "The server did not answer the command in time." : $"The connection broke: {ex.Message}");
        }
    }

    private async Task<int> SendAsync(int type, string body, CancellationToken cancel)
    {
        int id = ++_id;
        await _stream.WriteAsync(Encode(id, type, body), cancel);
        return id;
    }

    internal static byte[] Encode(int id, int type, string body)
    {
        byte[] text = Encoding.UTF8.GetBytes(body);
        var packet = new byte[4 + 4 + 4 + text.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        text.CopyTo(packet, 12);
        return packet;
    }

    private async Task<(int Id, int Type, string Body)> ReadAsync(CancellationToken cancel)
    {
        var head = new byte[4];
        await _stream.ReadExactlyAsync(head, cancel);
        int size = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (size is < 10 or > 16 << 20)
            throw new IOException($"Invalid RCON packet size {size}.");
        var rest = new byte[size];
        await _stream.ReadExactlyAsync(rest, cancel);
        return Decode(rest);
    }

    internal static (int Id, int Type, string Body) Decode(ReadOnlySpan<byte> packet) => (
        BinaryPrimitives.ReadInt32LittleEndian(packet),
        BinaryPrimitives.ReadInt32LittleEndian(packet[4..]),
        Encoding.UTF8.GetString(packet[8..^2]));

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
