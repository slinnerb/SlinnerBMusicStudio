using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SlinnerBMusicStudio;

// An in-process relay server, so the app can host a session itself (game-lobby
// style) without deploying the standalone server. Built on TcpListener with a
// minimal HTTP/1.1 implementation so it needs no admin rights or URL ACL
// (HttpListener's http://+:port/ binding would require elevation).
//
// Speaks the same protocol as the standalone server, so the normal
// SessionClient talks to it unchanged over http://<host>:<port>.
internal sealed class RelayHost : IDisposable
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLen = 8;
    private const int MaxBlobBytes = 200 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public bool IsRunning => _listener != null;

    private sealed class SessionState
    {
        public string Code = "";
        public long Version;
        public DateTime UpdatedAt = DateTime.UtcNow;
        public byte[] Blob = Array.Empty<byte>();
        public Dictionary<string, DateTime> Peers = new(StringComparer.OrdinalIgnoreCase);
    }

    // Starts listening. Tries the preferred port first, then an OS-assigned one.
    public void Start(int preferredPort)
    {
        if (IsRunning) return;
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Any, preferredPort);
            listener.Start();
        }
        catch (SocketException)
        {
            listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
        }
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener == null) return;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleClientSafe(client, ct), ct);
        }
    }

    private async Task HandleClientSafe(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using var net = client.GetStream();
                using var buffered = new BufferedStream(net, 16384);
                var req = await ReadRequestAsync(buffered, ct);
                if (req == null) return;
                var (status, statusText, headers, body) = Route(req);
                await WriteResponseAsync(net, status, statusText, headers, body, ct);
            }
        }
        catch { /* one bad client must not take down the host */ }
    }

    private sealed record RawRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    private static async Task<RawRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var header = new MemoryStream();
        int state = 0; // matches \r \n \r \n
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0) return null;
            byte b = one[0];
            header.WriteByte(b);
            state = b switch
            {
                13 => state == 2 ? 3 : 1,
                10 => state == 1 ? 2 : (state == 3 ? 4 : 0),
                _ => 0
            };
            if (state == 4) break;
            if (header.Length > 64 * 1024) return null;
        }

        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;
        var method = requestLine[0];
        var path = requestLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrEmpty(lines[i])) continue;
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            var key = lines[i][..colon].Trim();
            var val = lines[i][(colon + 1)..].Trim();
            headers[key] = val;
        }

        byte[] body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl) && cl > 0)
        {
            if (cl > MaxBlobBytes) return new RawRequest(method, path, headers, Array.Empty<byte>()); // too big; routed to 413
            body = new byte[cl];
            int off = 0;
            while (off < cl)
            {
                int n = await stream.ReadAsync(body.AsMemory(off, cl - off), ct);
                if (n == 0) break;
                off += n;
            }
        }
        return new RawRequest(method, path, headers, body);
    }

    private (int status, string statusText, Dictionary<string, string> headers, byte[] body) Route(RawRequest req)
    {
        var headers = new Dictionary<string, string>();
        var path = req.Path;
        int q = path.IndexOf('?');
        if (q >= 0) path = path[..q];

        if (req.Method == "GET" && path == "/health")
            return Ok(headers, "ok");

        if (req.Method == "POST" && path == "/sessions")
        {
            var s = CreateSession();
            headers["Content-Type"] = "application/json";
            var json = JsonSerializer.SerializeToUtf8Bytes(new { code = s.Code, version = s.Version });
            return (200, "OK", headers, json);
        }

        var parts = path.Trim('/').Split('/');
        if (parts.Length == 3 && parts[0].Equals("sessions", StringComparison.OrdinalIgnoreCase))
        {
            var code = parts[1].ToUpperInvariant();
            var resource = parts[2].ToLowerInvariant();

            if (req.Method == "GET" && resource == "info")
            {
                lock (_gate)
                {
                    headers["Content-Type"] = "application/json";
                    if (!_sessions.TryGetValue(code, out var s))
                        return (200, "OK", headers, JsonSerializer.SerializeToUtf8Bytes(
                            new { exists = false, version = 0, peers = Array.Empty<string>(), updatedAt = (DateTime?)null }));
                    var cutoff = DateTime.UtcNow.AddSeconds(-90);
                    var peers = s.Peers.Where(kv => kv.Value > cutoff).Select(kv => kv.Key).ToArray();
                    return (200, "OK", headers, JsonSerializer.SerializeToUtf8Bytes(
                        new { exists = true, version = s.Version, peers, updatedAt = (DateTime?)s.UpdatedAt }));
                }
            }

            if (req.Method == "GET" && resource == "blob")
            {
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(code, out var s))
                        return (404, "Not Found", headers, Array.Empty<byte>());
                    if (req.Headers.TryGetValue("X-Peer-Name", out var peer) && !string.IsNullOrWhiteSpace(peer))
                        s.Peers[peer] = DateTime.UtcNow;
                    headers["Content-Type"] = "application/octet-stream";
                    headers["X-Version"] = s.Version.ToString();
                    return (200, "OK", headers, s.Blob);
                }
            }

            if (req.Method == "PUT" && resource == "blob")
            {
                if (req.Headers.TryGetValue("Content-Length", out var clStr) &&
                    long.TryParse(clStr, out var cl) && cl > MaxBlobBytes)
                    return (413, "Payload Too Large", headers, Encoding.UTF8.GetBytes("project too large"));

                var expected = req.Headers.TryGetValue("X-Expected-Version", out var ev) && long.TryParse(ev, out var e) ? e : -1;
                var peer = req.Headers.TryGetValue("X-Peer-Name", out var p) ? p : "unknown";
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(code, out var s))
                        return (404, "Not Found", headers, Array.Empty<byte>());
                    if (expected >= 0 && expected != s.Version)
                    {
                        headers["X-Version"] = s.Version.ToString();
                        return (409, "Conflict", headers, Array.Empty<byte>());
                    }
                    s.Blob = req.Body;
                    s.Version++;
                    s.UpdatedAt = DateTime.UtcNow;
                    s.Peers[peer] = DateTime.UtcNow;
                    headers["X-Version"] = s.Version.ToString();
                    return Ok(headers, "ok");
                }
            }
        }

        return (404, "Not Found", headers, Encoding.UTF8.GetBytes("not found"));
    }

    private static (int, string, Dictionary<string, string>, byte[]) Ok(Dictionary<string, string> headers, string text)
    {
        if (!headers.ContainsKey("Content-Type")) headers["Content-Type"] = "text/plain; charset=utf-8";
        return (200, "OK", headers, Encoding.UTF8.GetBytes(text));
    }

    private SessionState CreateSession()
    {
        lock (_gate)
        {
            string code;
            do { code = NewCode(); } while (_sessions.ContainsKey(code));
            var s = new SessionState { Code = code, Version = 1 };
            _sessions[code] = s;
            return s;
        }
    }

    private static string NewCode()
    {
        Span<byte> bytes = stackalloc byte[CodeLen];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(CodeLen + 1);
        for (int i = 0; i < CodeLen; i++)
        {
            if (i == 4) sb.Append('-');
            sb.Append(Alphabet[bytes[i] & 0x1F]);
        }
        return sb.ToString();
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string statusText,
        Dictionary<string, string> headers, byte[] body, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {statusText}\r\n");
        foreach (var h in headers)
            sb.Append($"{h.Key}: {h.Value}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }
}
