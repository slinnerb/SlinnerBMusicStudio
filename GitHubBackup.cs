using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SlinnerBMusicStudio;

// Uploads a project blob to a GitHub repo via the contents API.
// PUT /repos/{owner}/{repo}/contents/{path}
// One commit per upload; existing files get the previous SHA looked up automatically.
internal sealed class GitHubBackup
{
    private readonly string _repoFullName;
    private readonly string _token;
    private readonly HttpClient _http;

    public GitHubBackup(string repoFullName, string token)
    {
        _repoFullName = repoFullName;
        _token = token;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SlinnerBMusicStudio", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    // Uploads a single file. Returns the commit URL.
    public async Task<string> UploadAsync(string path, byte[] content, string commitMessage)
    {
        var url = $"https://api.github.com/repos/{_repoFullName}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/")}";

        // Look up existing SHA so we can update rather than fail.
        string? sha = null;
        using (var get = await _http.GetAsync(url))
        {
            if (get.IsSuccessStatusCode)
            {
                var existingJson = await get.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.TryGetProperty("sha", out var s)) sha = s.GetString();
            }
        }

        var body = new Dictionary<string, object?>
        {
            ["message"] = commitMessage,
            ["content"] = Convert.ToBase64String(content)
        };
        if (sha != null) body["sha"] = sha;

        using var put = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(put);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"GitHub returned {(int)resp.StatusCode}: {err}");
        }

        var json = await resp.Content.ReadAsStringAsync();
        using var d = JsonDocument.Parse(json);
        if (d.RootElement.TryGetProperty("commit", out var commit) &&
            commit.TryGetProperty("html_url", out var html))
        {
            return html.GetString() ?? "";
        }
        return "";
    }

    // Quick auth check: GETs the repo metadata.
    public async Task EnsureReachableAsync()
    {
        using var resp = await _http.GetAsync($"https://api.github.com/repos/{_repoFullName}");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Backup repo '{_repoFullName}' not found (or token has no access).");
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("GitHub token rejected. Check the PAT and that it has the 'repo' scope.");
        resp.EnsureSuccessStatusCode();
    }
}
