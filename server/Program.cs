using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SlinnerBStudio.Server;

// Tiny relay server for SlinnerB's Music Studio sessions.
// Stores per-session project blobs in memory and on disk. No auth other than
// the session code itself, which is 8 random base32 chars (~40 bits of entropy).
//
// Endpoints:
//   POST /sessions                         create session, returns {code, version}
//   GET  /sessions/{code}/info             returns {exists, version, peers, updatedAt}
//   GET  /sessions/{code}/blob             returns binary project; version in X-Version header
//   PUT  /sessions/{code}/blob             body is binary project; headers X-Expected-Version, X-Peer-Name
//                                          409 with current version if mismatch
//   GET  /health                           returns "ok"
//
// All sessions are flushed to disk so a restart preserves state.

internal static class Program
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // base32 sans confusing chars
    private const int CodeLen = 8;
    private const int MaxBlobBytes = 200 * 1024 * 1024;                  // 200 MB cap per session

    private static readonly object _gate = new();
    private static readonly Dictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private static string _dataDir = "data";

    private sealed class SessionState
    {
        public string Code = "";
        public long Version;
        public DateTime UpdatedAt = DateTime.UtcNow;
        public byte[] Blob = Array.Empty<byte>();
        public Dictionary<string, DateTime> Peers = new(StringComparer.OrdinalIgnoreCase); // name -> last-seen UTC
    }

    private sealed record SessionInfo(bool Exists, long Version, string[] Peers, DateTime? UpdatedAt);
    private sealed record CreateResponse(string Code, long Version);

    private static int Main(string[] args)
    {
        var port = 8090;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) port = p;
            if (args[i] == "--data") _dataDir = args[i + 1];
        }

        Directory.CreateDirectory(_dataDir);
        LoadFromDisk();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"Could not bind http://+:{port}/  ({ex.Message})");
            Console.Error.WriteLine("Run as Administrator or grant URL ACL:");
            Console.Error.WriteLine($"  netsh http add urlacl url=http://+:{port}/ user=Everyone");
            return 1;
        }

        Console.WriteLine($"SlinnerB Studio relay listening on port {port}, data in {Path.GetFullPath(_dataDir)}");
        Console.WriteLine($"{_sessions.Count} session(s) loaded.");

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { break; }
            _ = Task.Run(() => HandleSafe(ctx));
        }
        return 0;
    }

    private static async Task HandleSafe(HttpListenerContext ctx)
    {
        try { await Handle(ctx); }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 500;
                await WriteText(ctx, ex.Message);
            }
            catch { /* response already started */ }
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { }
        }
    }

    private static async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "";
        var method = ctx.Request.HttpMethod;

        if (method == "GET" && path == "/health")
        {
            await WriteText(ctx, "ok");
            return;
        }

        if (method == "POST" && path == "/sessions")
        {
            var session = CreateSession();
            ctx.Response.ContentType = "application/json";
            await WriteJson(ctx, new CreateResponse(session.Code, session.Version));
            return;
        }

        // /sessions/{code}/{info|blob}
        var parts = path.Trim('/').Split('/');
        if (parts.Length == 3 && parts[0].Equals("sessions", StringComparison.OrdinalIgnoreCase))
        {
            var code = parts[1].ToUpperInvariant();
            var resource = parts[2].ToLowerInvariant();

            if (method == "GET" && resource == "info")
            {
                await WriteJson(ctx, BuildInfo(code));
                return;
            }
            if (method == "GET" && resource == "blob")
            {
                await ReadBlob(ctx, code);
                return;
            }
            if (method == "PUT" && resource == "blob")
            {
                await WriteBlob(ctx, code);
                return;
            }
        }

        ctx.Response.StatusCode = 404;
        await WriteText(ctx, "not found");
    }

    private static SessionState CreateSession()
    {
        lock (_gate)
        {
            string code;
            do { code = NewCode(); } while (_sessions.ContainsKey(code));
            var s = new SessionState { Code = code, Version = 1 };
            _sessions[code] = s;
            PersistLocked(s);
            return s;
        }
    }

    private static SessionInfo BuildInfo(string code)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(code, out var s))
                return new SessionInfo(false, 0, Array.Empty<string>(), null);

            // Peers seen in the last 90 seconds are "in the session right now".
            var cutoff = DateTime.UtcNow.AddSeconds(-90);
            var peers = s.Peers.Where(kv => kv.Value > cutoff).Select(kv => kv.Key).ToArray();
            return new SessionInfo(true, s.Version, peers, s.UpdatedAt);
        }
    }

    private static async Task ReadBlob(HttpListenerContext ctx, string code)
    {
        byte[] blob; long version;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(code, out var s))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            blob = s.Blob;
            version = s.Version;

            var peer = ctx.Request.Headers["X-Peer-Name"];
            if (!string.IsNullOrWhiteSpace(peer)) s.Peers[peer] = DateTime.UtcNow;
        }
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers["X-Version"] = version.ToString();
        ctx.Response.ContentLength64 = blob.Length;
        await ctx.Response.OutputStream.WriteAsync(blob);
    }

    private static async Task WriteBlob(HttpListenerContext ctx, string code)
    {
        if (ctx.Request.ContentLength64 > MaxBlobBytes)
        {
            ctx.Response.StatusCode = 413;
            await WriteText(ctx, $"project too large (limit {MaxBlobBytes} bytes)");
            return;
        }

        using var ms = new MemoryStream();
        await ctx.Request.InputStream.CopyToAsync(ms);
        var blob = ms.ToArray();

        var expected = long.TryParse(ctx.Request.Headers["X-Expected-Version"], out var ev) ? ev : -1;
        var peer = ctx.Request.Headers["X-Peer-Name"] ?? "unknown";

        SessionState s;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(code, out var found))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            s = found;

            if (expected >= 0 && expected != s.Version)
            {
                ctx.Response.StatusCode = 409;
                ctx.Response.Headers["X-Version"] = s.Version.ToString();
                return;
            }

            s.Blob = blob;
            s.Version++;
            s.UpdatedAt = DateTime.UtcNow;
            s.Peers[peer] = DateTime.UtcNow;
            PersistLocked(s);
        }

        ctx.Response.Headers["X-Version"] = s.Version.ToString();
        await WriteText(ctx, "ok");
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

    private static void PersistLocked(SessionState s)
    {
        var blobPath = Path.Combine(_dataDir, s.Code + ".blob");
        var metaPath = Path.Combine(_dataDir, s.Code + ".json");
        File.WriteAllBytes(blobPath, s.Blob);
        var meta = new { code = s.Code, version = s.Version, updatedAt = s.UpdatedAt };
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));
    }

    private static void LoadFromDisk()
    {
        foreach (var metaFile in Directory.EnumerateFiles(_dataDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(metaFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var code = root.GetProperty("code").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(code)) continue;
                var version = root.GetProperty("version").GetInt64();
                var updated = root.GetProperty("updatedAt").GetDateTime();
                var blobPath = Path.Combine(_dataDir, code + ".blob");
                var blob = File.Exists(blobPath) ? File.ReadAllBytes(blobPath) : Array.Empty<byte>();
                _sessions[code] = new SessionState
                {
                    Code = code, Version = version, UpdatedAt = updated, Blob = blob
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Skipping {metaFile}: {ex.Message}");
            }
        }
    }

    private static Task WriteText(HttpListenerContext ctx, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        return ctx.Response.OutputStream.WriteAsync(bytes).AsTask();
    }

    private static Task WriteJson<T>(HttpListenerContext ctx, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        return ctx.Response.OutputStream.WriteAsync(bytes).AsTask();
    }
}
