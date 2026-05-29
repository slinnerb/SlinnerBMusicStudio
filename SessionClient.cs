using System.Net.Http;
using System.Text.Json;

namespace SlinnerBMusicStudio;

// HTTP client for the SlinnerBStudio relay server.
internal sealed class SessionClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _peerName;

    public SessionClient(string baseUrl, string peerName)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _peerName = string.IsNullOrWhiteSpace(peerName) ? "anonymous" : peerName;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public sealed record CreateResponse(string Code, long Version);
    public sealed record SessionInfo(bool Exists, long Version, string[] Peers, DateTime? UpdatedAt);
    public sealed record BlobResult(byte[] Data, long Version);

    public async Task<CreateResponse> CreateSessionAsync()
    {
        using var resp = await _http.PostAsync($"{_baseUrl}/sessions", content: null);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<CreateResponse>(json, opts)
            ?? throw new InvalidOperationException("Empty response from server.");
    }

    public async Task<SessionInfo> GetInfoAsync(string code)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/sessions/{code}/info");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new SessionInfo(false, 0, Array.Empty<string>(), null);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<SessionInfo>(json, opts)
            ?? new SessionInfo(false, 0, Array.Empty<string>(), null);
    }

    public async Task<BlobResult> DownloadBlobAsync(string code)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/sessions/{code}/blob");
        req.Headers.Add("X-Peer-Name", _peerName);
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("Session not found.");
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var version = ParseVersion(resp);
        return new BlobResult(bytes, version);
    }

    public sealed class VersionConflictException : Exception
    {
        public long ServerVersion { get; }
        public VersionConflictException(long serverVersion)
            : base($"Server has a newer version ({serverVersion}).") { ServerVersion = serverVersion; }
    }

    public async Task<long> UploadBlobAsync(string code, byte[] data, long expectedVersion)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}/sessions/{code}/blob")
        {
            Content = new ByteArrayContent(data)
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        req.Headers.Add("X-Peer-Name", _peerName);
        req.Headers.Add("X-Expected-Version", expectedVersion.ToString());

        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new VersionConflictException(ParseVersion(resp));
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("Session not found on server.");
        resp.EnsureSuccessStatusCode();
        return ParseVersion(resp);
    }

    private static long ParseVersion(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("X-Version", out var values))
        {
            foreach (var v in values)
                if (long.TryParse(v, out var n)) return n;
        }
        return 0;
    }
}
