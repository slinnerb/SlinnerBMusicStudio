using System.Diagnostics;
using System.IO.Compression;
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

        var canAutoInstall = FindClientAsset(latest) != null;

        if (canAutoInstall)
        {
            var result = MessageBox.Show(owner,
                "A new version is available." + Environment.NewLine +
                $"Installed: {current}" + Environment.NewLine +
                $"Available: {latest.Version}" + notes + Environment.NewLine + Environment.NewLine +
                "Download and install it now? The app will close and reopen automatically." + Environment.NewLine +
                "(Choose \"No\" to open the download page instead.)",
                "Update available", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);

            if (result == DialogResult.Yes)
            {
                DownloadAndInstall(owner, latest);
                return;
            }
            if (result == DialogResult.No)
                OpenPage(owner, latest.HtmlUrl);
            return;
        }

        // No matching asset to auto-install — fall back to opening the page.
        var manual = MessageBox.Show(owner,
            "A new version is available." + Environment.NewLine +
            $"Installed: {current}" + Environment.NewLine +
            $"Available: {latest.Version}" + notes + Environment.NewLine + Environment.NewLine +
            "Open the download page in your browser?",
            "Update available", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (manual == DialogResult.OK) OpenPage(owner, latest.HtmlUrl);
    }

    private static void OpenPage(IWin32Window owner, string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(owner,
                $"Could not open the browser.{Environment.NewLine}{Environment.NewLine}URL: {url}{Environment.NewLine}{ex.Message}",
                "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // The client zip in a release (not the server zip). Matches names like
    // "SlinnerBs-Music-Studio-v1.4.0.zip" and excludes "SlinnerBStudio-Server-...".
    private static ReleaseAsset? FindClientAsset(ReleaseInfo info) =>
        info.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("Music-Studio", StringComparison.OrdinalIgnoreCase));

    private static void DownloadAndInstall(IWin32Window owner, ReleaseInfo info)
    {
        var asset = FindClientAsset(info);
        if (asset == null) { OpenPage(owner, info.HtmlUrl); return; }

        var temp = Path.Combine(Path.GetTempPath(), "SlinnerBUpdate");
        var zipPath = Path.Combine(temp, "download.zip");
        var staging = Path.Combine(temp, "staging");
        try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        Directory.CreateDirectory(temp);

        Exception? error = null;
        bool cancelled = false;

        using (var dlg = new DownloadProgressForm())
        {
            dlg.Load += async (_, _) =>
            {
                try
                {
                    await DownloadFileAsync(asset.DownloadUrl, zipPath, dlg);
                    dlg.SetStatus("Extracting…");
                    Directory.CreateDirectory(staging);
                    await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true));
                    dlg.SetStatus("Installing… the app will restart.");
                    LaunchUpdaterAndExit(staging, temp);   // never returns
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    dlg.CloseFromWorker();
                }
                catch (Exception ex)
                {
                    error = ex;
                    dlg.CloseFromWorker();
                }
            };
            dlg.ShowDialog(owner);
        }

        if (cancelled) return;
        if (error != null)
        {
            MessageBox.Show(owner,
                "Update failed.\n\n" + error.Message +
                "\n\nYou can still update manually from the download page.",
                "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            OpenPage(owner, info.HtmlUrl);
        }
    }

    private static async Task DownloadFileAsync(string url, string destPath, DownloadProgressForm dlg)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, dlg.Token);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;

        using var src = await resp.Content.ReadAsStreamAsync(dlg.Token);
        using var dst = File.Create(destPath);
        var buffer = new byte[81920];
        long done = 0, lastReport = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, dlg.Token)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), dlg.Token);
            done += n;
            if (done - lastReport >= 1_000_000 || done == total)
            {
                dlg.ReportProgress(done, total);
                lastReport = done;
            }
        }
    }

    // Writes a small batch script that waits for this process to exit, copies the
    // staged files over the install folder, relaunches the app, and cleans up.
    private static void LaunchUpdaterAndExit(string staging, string temp)
    {
        var exePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not determine the running executable path.");
        var installDir = Path.GetDirectoryName(exePath)!;
        var exeName = Path.GetFileName(exePath);
        var pid = Environment.ProcessId;
        var scriptPath = Path.Combine(Path.GetTempPath(), "SlinnerB-update.bat");
        var log = Path.Combine(temp, "update.log");

        var script =
$@"@echo off
:waitloop
tasklist /FI ""PID eq {pid}"" | find ""{pid}"" >nul
if %errorlevel%==0 (
  timeout /t 1 /nobreak >nul
  goto waitloop
)
timeout /t 1 /nobreak >nul
robocopy ""{staging}"" ""{installDir}"" /E /IS /IT /R:3 /W:1 /LOG:""{log}"" >nul
start """" ""{Path.Combine(installDir, exeName)}""
rmdir /S /Q ""{temp}""
del ""%~f0""
";
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false
        });

        Environment.Exit(0);
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

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assetsEl.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var dl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(dl))
                    assets.Add(new ReleaseAsset(name, dl));
            }
        }

        if (!TryParseTag(tag, out var version)) return null;
        return new ReleaseInfo(version, url, notes, assets);
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

    private sealed record ReleaseAsset(string Name, string DownloadUrl);
    private sealed record ReleaseInfo(Version Version, string HtmlUrl, string Notes, List<ReleaseAsset> Assets);

    // Small modal progress window for the download/extract step.
    private sealed class DownloadProgressForm : Form
    {
        private readonly ProgressBar _bar = new();
        private readonly Label _label = new();
        private readonly CancellationTokenSource _cts = new();

        public CancellationToken Token => _cts.Token;

        public DownloadProgressForm()
        {
            Text = "Updating SlinnerB's Music Studio";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(440, 116);

            _label.Text = "Starting download…";
            _label.Location = new Point(16, 16);
            _label.AutoSize = true;

            _bar.Location = new Point(16, 44);
            _bar.Size = new Size(408, 22);
            _bar.Maximum = 1000;
            _bar.Style = ProgressBarStyle.Marquee;

            var cancel = new Button { Text = "Cancel", Location = new Point(348, 78), Width = 76 };
            cancel.Click += (_, _) => { try { _cts.Cancel(); } catch { } };

            Controls.AddRange(new Control[] { _label, _bar, cancel });
        }

        public void SetStatus(string text)
        {
            if (InvokeRequired) BeginInvoke(new Action(() => _label.Text = text));
            else _label.Text = text;
        }

        public void ReportProgress(long done, long total)
        {
            void Apply()
            {
                if (total > 0)
                {
                    _bar.Style = ProgressBarStyle.Continuous;
                    _bar.Value = (int)Math.Min(1000, 1000 * done / total);
                    _label.Text = $"Downloading…  {done / 1048576} MB / {total / 1048576} MB";
                }
                else
                {
                    _label.Text = $"Downloading…  {done / 1048576} MB";
                }
            }
            if (InvokeRequired) BeginInvoke(new Action(Apply));
            else Apply();
        }

        public void CloseFromWorker()
        {
            if (InvokeRequired) BeginInvoke(new Action(Close));
            else Close();
        }
    }
}
