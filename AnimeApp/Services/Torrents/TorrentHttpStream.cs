using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AnimeApp.Services.Torrents;

// A private, capability-addressed endpoint. No filesystem paths or proxy URLs are accepted.
public sealed class TorrentHttpStream : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim reading = new(1);
    private readonly Stream stream;
    private readonly string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly Task loop;
    private CancellationTokenSource? currentRead;
    private readonly object sync = new();
    public string Url { get; }
    public TorrentHttpStream(Stream stream)
    {
        this.stream = stream;
        listener.Start(8);
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/{key}";
        loop = Accept();
    }
    private async Task Accept()
    {
        try { while (!shutdown.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(shutdown.Token); _ = Handle(client); } }
        catch (OperationCanceledException) { }
        catch (SocketException) when (shutdown.IsCancellationRequested) { }
    }
    public static bool TryRange(string? value, long length, out long start, out long end)
    {
        start = 0; end = length - 1;
        if (length <= 0) return false;
        if (value is null) return true;
        if (!value.StartsWith("bytes=", StringComparison.Ordinal) || value.Contains(',')) return false;
        var parts = value[6..].Split('-');
        if (parts.Length != 2) return false;
        if (parts[0] == "") {
            if (!long.TryParse(parts[1], out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix); return true;
        }
        if (!long.TryParse(parts[0], out start) || start < 0 || start >= length) return false;
        if (parts[1] != "" && (!long.TryParse(parts[1], out end) || end < start)) return false;
        end = Math.Min(end, length - 1); return true;
    }
    private async Task Handle(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try {
                var network = client.GetStream();
                var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 8192) {
                    if (await network.ReadAsync(one, deadline.Token) == 0) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var first = lines[0].Split(' ');
                var headers = lines.Skip(1).Where(l => l.Contains(':')).Select(l => l.Split(':', 2)).ToArray();
                string? Get(string name) => headers.FirstOrDefault(h => h[0].Equals(name, StringComparison.OrdinalIgnoreCase))?[1].Trim();
                var expectedHost = new Uri(Url).Authority;
                var valid = first.Length == 3 && first[1] == "/" + key && Get("Host") == expectedHost && Get("Origin") is null;
                var method = first[0];
                if (!valid || method is not ("GET" or "HEAD")) { await Reply(network, "403 Forbidden", "Content-Length: 0\r\n", deadline.Token); return; }
                var range = Get("Range");
                if (!TryRange(range, stream.Length, out var start, out var end)) {
                    await Reply(network, "416 Range Not Satisfiable", $"Content-Range: bytes */{stream.Length}\r\nContent-Length: 0\r\n", deadline.Token); return;
                }
                await Reply(network, range is null ? "200 OK" : "206 Partial Content", $"Accept-Ranges: bytes\r\nContent-Type: application/octet-stream\r\nContent-Length: {end-start+1}\r\n" + (range is null ? "" : $"Content-Range: bytes {start}-{end}/{stream.Length}\r\n"), deadline.Token);
                if (method == "HEAD") return;
                using var transfer = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                lock (sync) { currentRead?.Cancel(); currentRead = transfer; }
                var acquired = false;
                try {
                    await reading.WaitAsync(transfer.Token); acquired = true;
                    stream.Seek(start, SeekOrigin.Begin);
                    var buffer = new byte[65536]; var remaining = end-start+1;
                    while (remaining > 0) {
                        transfer.CancelAfter(TimeSpan.FromSeconds(45));
                        var count = await stream.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)), transfer.Token);
                        if (count == 0) break;
                        await network.WriteAsync(buffer.AsMemory(0,count), transfer.Token);
                        remaining -= count;
                    }
                } finally { if (acquired) reading.Release(); lock(sync) { if (currentRead == transfer) currentRead = null; } }
            } catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }
    private static Task Reply(Stream s, string status, string headers, CancellationToken ct) => s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nCache-Control: no-store\r\n{headers}\r\n"),ct).AsTask();
    public async ValueTask DisposeAsync() { shutdown.Cancel(); listener.Stop(); await loop; await reading.WaitAsync(); try { stream.Dispose(); } finally { reading.Release(); } }
}
