using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hostbay.Core;

/// <summary>Source RCON protocol client (Palworld, ARK, V Rising, Conan, Minecraft, Zomboid...).</summary>
public static class RconClient
{
    private const int TypeAuth = 3, TypeExec = 2, TypeAuthResponse = 2;

    public static async Task<string> ExecuteAsync(RemoteConsoleSettings cfg, string command, CancellationToken ct = default)
    {
        if (cfg.Port <= 0) throw new InvalidOperationException("RCON port is not configured.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(string.IsNullOrWhiteSpace(cfg.Host) ? "127.0.0.1" : cfg.Host, cfg.Port, cts.Token);
        var stream = tcp.GetStream();

        await WritePacket(stream, 1, TypeAuth, cfg.Password, cts.Token);
        // Some servers send an empty RESPONSE_VALUE before the AUTH_RESPONSE.
        while (true)
        {
            var (id, type, _) = await ReadPacket(stream, cts.Token);
            if (type == TypeAuthResponse || id == -1)
            {
                if (id == -1) throw new UnauthorizedAccessException("RCON authentication failed. Check the password.");
                break;
            }
        }

        await WritePacket(stream, 2, TypeExec, command, cts.Token);
        var sb = new StringBuilder();
        // Read the first response, then keep draining briefly for multi-packet replies.
        var first = await ReadPacket(stream, cts.Token);
        sb.Append(first.Body);
        while (true)
        {
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drain.CancelAfter(250);
            try
            {
                var more = await ReadPacket(stream, drain.Token);
                sb.Append(more.Body);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
        }
        return sb.ToString().TrimEnd();
    }

    private static async Task WritePacket(NetworkStream s, int id, int type, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var buf = new byte[14 + bytes.Length];
        BitConverter.GetBytes(10 + bytes.Length).CopyTo(buf, 0);
        BitConverter.GetBytes(id).CopyTo(buf, 4);
        BitConverter.GetBytes(type).CopyTo(buf, 8);
        bytes.CopyTo(buf, 12);
        await s.WriteAsync(buf, ct);
    }

    private static async Task<(int Id, int Type, string Body)> ReadPacket(NetworkStream s, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactly(s, header, ct);
        int size = BitConverter.ToInt32(header, 0);
        if (size < 10 || size > 1_000_000) throw new IOException("Invalid RCON packet.");
        var data = new byte[size];
        await ReadExactly(s, data, ct);
        int id = BitConverter.ToInt32(data, 0);
        int type = BitConverter.ToInt32(data, 4);
        var body = Encoding.UTF8.GetString(data, 8, Math.Max(0, size - 10));
        return (id, type, body);
    }

    private static async Task ReadExactly(NetworkStream s, byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException("RCON connection closed.");
            read += n;
        }
    }
}

/// <summary>Line-based Telnet console client (7 Days to Die).</summary>
public static class TelnetClient
{
    public static async Task<string> ExecuteAsync(RemoteConsoleSettings cfg, string command, CancellationToken ct = default)
    {
        if (cfg.Port <= 0) throw new InvalidOperationException("Telnet port is not configured.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(string.IsNullOrWhiteSpace(cfg.Host) ? "127.0.0.1" : cfg.Host, cfg.Port, cts.Token);
        var stream = tcp.GetStream();

        var greeting = await ReadFor(stream, TimeSpan.FromMilliseconds(1200), ct);
        if (greeting.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            await Send(stream, cfg.Password, cts.Token);
            var auth = await ReadFor(stream, TimeSpan.FromMilliseconds(1500), ct);
            if (auth.Contains("incorrect", StringComparison.OrdinalIgnoreCase) || auth.Contains("wrong", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Telnet password was rejected.");
        }
        await Send(stream, command, cts.Token);
        var reply = await ReadFor(stream, TimeSpan.FromMilliseconds(1500), ct);
        try { await Send(stream, "exit", CancellationToken.None); } catch { }
        return reply.Trim();
    }

    private static Task Send(NetworkStream s, string line, CancellationToken ct) =>
        s.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), ct).AsTask();

    private static async Task<string> ReadFor(NetworkStream s, TimeSpan window, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        try
        {
            while (true)
            {
                int n = await s.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return sb.ToString();
    }
}
