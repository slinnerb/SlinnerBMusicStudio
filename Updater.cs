using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace SlinnerBMusicStudio;

internal static class Updater
{
    private const string RepoOwner = "slinnerb";
    private const string RepoName = "SlinnerBMusicStudio";
    private const string UserAgent = "SlinnerBMusicStudio-Updater";

    private static string LatestReleaseUrl =>
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static async Task CheckAsync(IWin32Window owner, bool showWhenNoUpdate)
    {
        ReleaseInfo? latest;
        try
        {
            latest = await FetchLatestAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner,
                $"Could not check for updates.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (latest is null)
        {
            if (showWhenNoUpdate)
                MessageBox.Show(owner, "No releases have been published yet.",
                    "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var current = CurrentVersion;
        if (latest.Version <= current)
        {
            if (showWhenNoUpdate)
                MessageBox.Show(owner,
                    $"You are running the latest version ({current}).",
                    "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var notes = string.IsNullOrWhiteSpace(latest.Notes)
            ? string.Empty
            : Environment.NewLine + Environment.NewLine + "What's new:" + Environment.NewLine + Truncate(latest.Notes, 600);

        var result = MessageBox.Show(owner,
            $"A new version is available." + Environment.NewLine +
            $"Installed: {current}" + Environment.NewLine +
            $"Available: {latest.Version}" + notes + Environment.NewLine + Environment.NewLine +
            "Open the download page in your browser?",
            "Update available", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);

        if (result == DialogResult.OK)
        {
            try
            {
                Process.Start(new ProcessStartInfo(latest.HtmlUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner,
                    $"Could not open the browser.{Environment.NewLine}{Environment.NewLine}URL: {latest.HtmlUrl}{Environment.NewLine}{ex.Message}",
                    "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private static async Task<ReleaseInfo?> FetchLatestAsync()
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await http.GetAsync(LatestReleaseUrl);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        if (!TryParseTag(tag, out var version)) return null;
        return new ReleaseInfo(version, url, notes);
    }

    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var trimmed = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(trimmed, out version!);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    private sealed record ReleaseInfo(Version Version, string HtmlUrl, string Notes);
}
