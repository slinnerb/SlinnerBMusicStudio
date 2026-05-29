namespace SlinnerBMusicStudio;

// "Work with Friends" menu handlers and session state.
// Sync model: each session is a project blob on the relay server with a version
// counter. To pick up others' edits, click Sync (which pulls). To share yours,
// click Sync (which pushes). Conflicts are surfaced as a dialog asking which
// side to keep.
public partial class MainForm
{
    private string? _sessionCode;
    private long _sessionVersion;

    private bool InSession => !string.IsNullOrEmpty(_sessionCode);

    private SessionClient? BuildClientOrWarn()
    {
        var url = _settings.ServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this,
                "No server URL is configured. Open Work with Friends → Session Settings and paste the address your host gave you (for example, http://yourserver.com:8090).",
                "Server not configured", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        var peer = string.IsNullOrWhiteSpace(_settings.PeerName) ? Environment.UserName : _settings.PeerName!;
        return new SessionClient(url, peer);
    }

    private async void CreateSession_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle) return;
        var client = BuildClientOrWarn();
        if (client is null) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            var created = await client.CreateSessionAsync();
            _sessionCode = created.Code;
            _sessionVersion = created.Version;

            // Push the current project so the session starts from what's open.
            var blob = _project.ToBytes();
            _sessionVersion = await client.UploadBlobAsync(_sessionCode, blob, expectedVersion: _sessionVersion);
            UpdateTitle();

            var copy = MessageBox.Show(this,
                $"Session created. Share this code with your friends:{Environment.NewLine}{Environment.NewLine}" +
                $"     {_sessionCode}{Environment.NewLine}{Environment.NewLine}" +
                "Click OK to copy it to your clipboard.",
                "Session created", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (copy == DialogResult.OK)
            {
                try { Clipboard.SetText(_sessionCode); } catch { }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not create session.\n\n" + ex.Message,
                "Work with Friends", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Cursor = Cursors.Default; }
    }

    private async void JoinSession_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !ConfirmDiscard()) return;
        var client = BuildClientOrWarn();
        if (client is null) return;

        var code = PromptForCode();
        if (string.IsNullOrWhiteSpace(code)) return;
        code = code.Trim().ToUpperInvariant();

        try
        {
            Cursor = Cursors.WaitCursor;
            var info = await client.GetInfoAsync(code);
            if (!info.Exists)
            {
                MessageBox.Show(this, "No session with that code is on the server.",
                    "Join session", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var result = await client.DownloadBlobAsync(code);
            if (result.Data.Length == 0)
            {
                _project.Clear();
            }
            else
            {
                _project.LoadFromBytes(result.Data);
            }
            _sessionCode = code;
            _sessionVersion = result.Version;
            _currentPath = null;
            _dirty = false;
            waveform.ActiveTrack = 0;
            waveform.SetCursor(0);
            waveform.ZoomToFit();
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not join session.\n\n" + ex.Message,
                "Work with Friends", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Cursor = Cursors.Default; }
    }

    private async void SyncSession_Click(object? sender, EventArgs e)
    {
        if (!InSession)
        {
            MessageBox.Show(this, "You aren't in a session. Use \"Create New Song\" or \"Join Music Session\" first.",
                "Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_state != AppState.Idle)
        {
            MessageBox.Show(this, "Stop recording or playback before syncing.",
                "Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var client = BuildClientOrWarn();
        if (client is null) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            var info = await client.GetInfoAsync(_sessionCode!);
            if (!info.Exists)
            {
                MessageBox.Show(this, "This session no longer exists on the server.",
                    "Sync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Case 1: server has changes we don't.
            if (info.Version > _sessionVersion)
            {
                if (_dirty)
                {
                    var choice = MessageBox.Show(this,
                        $"Your friends have made changes (server version {info.Version}, you have {_sessionVersion}).\n" +
                        "Your local edits aren't saved yet.\n\n" +
                        "Yes = download theirs and lose your changes.\n" +
                        "No  = upload yours and overwrite theirs.\n" +
                        "Cancel = do nothing.",
                        "Conflict", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (choice == DialogResult.Cancel) return;
                    if (choice == DialogResult.Yes)
                    {
                        await PullAsync(client);
                        return;
                    }
                    // No -> fall through to push, overwriting server.
                }
                else
                {
                    await PullAsync(client);
                    return;
                }
            }

            // Case 2: push our changes.
            var blob = _project.ToBytes();
            try
            {
                _sessionVersion = await client.UploadBlobAsync(_sessionCode!, blob, expectedVersion: _sessionVersion);
                _dirty = false;
                UpdateTitle();
                statusLabel.Text = $"Synced. Server version {_sessionVersion}. " +
                                   (info.Peers.Length > 0 ? "In session: " + string.Join(", ", info.Peers) : "No other peers right now.");
            }
            catch (SessionClient.VersionConflictException)
            {
                // Race: someone uploaded between our info-check and our upload. Force overwrite.
                _sessionVersion = await client.UploadBlobAsync(_sessionCode!, blob, expectedVersion: -1);
                _dirty = false;
                UpdateTitle();
            }

            // Fire-and-forget auto-backup so a slow GitHub call doesn't block the user.
            if (_settings.AutoBackup) _ = TryAutoBackupAsync(blob);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Sync failed.\n\n" + ex.Message,
                "Work with Friends", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Cursor = Cursors.Default; }
    }

    private async Task PullAsync(SessionClient client)
    {
        var result = await client.DownloadBlobAsync(_sessionCode!);
        if (result.Data.Length == 0) _project.Clear();
        else _project.LoadFromBytes(result.Data);
        _sessionVersion = result.Version;
        _dirty = false;
        waveform.SetCursor(0);
        UpdateTitle();
        statusLabel.Text = $"Pulled server version {_sessionVersion}.";
    }

    private void LeaveSession_Click(object? sender, EventArgs e)
    {
        if (!InSession)
        {
            MessageBox.Show(this, "You aren't in a session.", "Work with Friends",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _sessionCode = null;
        _sessionVersion = 0;
        UpdateTitle();
        statusLabel.Text = "Left session.";
    }

    private void SessionSettings_Click(object? sender, EventArgs e)
    {
        using var dlg = new Form
        {
            Text = "Session Settings",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(460, 400),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };

        var urlLabel = new Label { Text = "Relay server URL:", Location = new Point(16, 16), AutoSize = true };
        var urlBox = new TextBox
        {
            Location = new Point(16, 36),
            Width = 428,
            Text = _settings.ServerUrl ?? "http://"
        };
        var urlHint = new Label
        {
            Text = "Example:  http://yourserver.example.com:8090",
            Location = new Point(16, 62),
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        };

        var nameLabel = new Label { Text = "Display name (what friends see):", Location = new Point(16, 86), AutoSize = true };
        var nameBox = new TextBox
        {
            Location = new Point(16, 106),
            Width = 240,
            Text = _settings.PeerName ?? Environment.UserName
        };

        var backupHeader = new Label
        {
            Text = "GitHub auto-backup (optional)",
            Location = new Point(16, 148),
            AutoSize = true,
            Font = new Font(this.Font, FontStyle.Bold)
        };
        var repoLabel = new Label { Text = "Backup repo (owner/name, private):", Location = new Point(16, 172), AutoSize = true };
        var repoBox = new TextBox
        {
            Location = new Point(16, 192),
            Width = 280,
            Text = _settings.BackupRepo ?? ""
        };
        var tokenLabel = new Label { Text = "Personal Access Token (with \"repo\" scope):", Location = new Point(16, 220), AutoSize = true };
        var tokenBox = new TextBox
        {
            Location = new Point(16, 240),
            Width = 428,
            UseSystemPasswordChar = true,
            Text = _settings.BackupToken ?? ""
        };
        var tokenHint = new Label
        {
            Text = "Create one at  https://github.com/settings/tokens  ('repo' scope, classic token)",
            Location = new Point(16, 266),
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        };
        var autoBox = new CheckBox
        {
            Text = "Auto-backup every successful Sync",
            Location = new Point(16, 290),
            AutoSize = true,
            Checked = _settings.AutoBackup
        };
        var testBtn = new Button
        {
            Text = "Test Connection",
            Location = new Point(16, 318),
            Width = 140
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(288, 360), Width = 76 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(368, 360), Width = 76 };
        dlg.Controls.AddRange(new Control[]
        {
            urlLabel, urlBox, urlHint,
            nameLabel, nameBox,
            backupHeader, repoLabel, repoBox, tokenLabel, tokenBox, tokenHint, autoBox, testBtn,
            ok, cancel
        });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        testBtn.Click += async (_, _) =>
        {
            var repo = repoBox.Text.Trim();
            var token = tokenBox.Text.Trim();
            if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(token))
            {
                MessageBox.Show(dlg, "Fill in the repo and token first.", "Test Connection",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                testBtn.Enabled = false;
                await new GitHubBackup(repo, token).EnsureReachableAsync();
                MessageBox.Show(dlg, $"OK — '{repo}' is reachable and the token works.",
                    "Test Connection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(dlg, ex.Message, "Test Connection",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { testBtn.Enabled = true; }
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var url = urlBox.Text.Trim();
        if (!string.IsNullOrEmpty(url) && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        _settings.ServerUrl = string.IsNullOrEmpty(url) ? null : url;
        _settings.PeerName = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim();
        _settings.BackupRepo = string.IsNullOrWhiteSpace(repoBox.Text) ? null : repoBox.Text.Trim();
        _settings.BackupToken = string.IsNullOrWhiteSpace(tokenBox.Text) ? null : tokenBox.Text.Trim();
        _settings.AutoBackup = autoBox.Checked;
        _settings.Save();
    }

    private async void BackupNow_Click(object? sender, EventArgs e)
    {
        if (_project.IsEmpty)
        {
            MessageBox.Show(this, "Nothing to back up — the project is empty.",
                "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.BackupRepo) || string.IsNullOrWhiteSpace(_settings.BackupToken))
        {
            MessageBox.Show(this,
                "GitHub backup isn't configured. Open Work with Friends → Session Settings and fill in the backup repo + token.",
                "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;
            var commitUrl = await UploadBackupAsync(_project.ToBytes());
            statusLabel.Text = "Backed up to GitHub.";
            MessageBox.Show(this,
                "Backup uploaded." + (string.IsNullOrEmpty(commitUrl) ? "" : Environment.NewLine + Environment.NewLine + commitUrl),
                "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Backup failed.\n\n" + ex.Message,
                "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Cursor = Cursors.Default; }
    }

    private async Task TryAutoBackupAsync(byte[] blob)
    {
        if (string.IsNullOrWhiteSpace(_settings.BackupRepo) || string.IsNullOrWhiteSpace(_settings.BackupToken))
            return;
        try
        {
            await UploadBackupAsync(blob);
            BeginInvoke(new Action(() => statusLabel.Text = statusLabel.Text + "  (backup ok)"));
        }
        catch (Exception ex)
        {
            BeginInvoke(new Action(() => statusLabel.Text = "Backup failed: " + ex.Message));
        }
    }

    private async Task<string> UploadBackupAsync(byte[] blob)
    {
        var backup = new GitHubBackup(_settings.BackupRepo!, _settings.BackupToken!);
        var sessionFolder = string.IsNullOrEmpty(_sessionCode) ? "solo" : _sessionCode!;
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var path = $"sessions/{sessionFolder}/{timestamp}.smsproj";
        var peer = string.IsNullOrWhiteSpace(_settings.PeerName) ? Environment.UserName : _settings.PeerName!;
        var msg = string.IsNullOrEmpty(_sessionCode)
            ? $"Snapshot from {peer} ({timestamp} UTC)"
            : $"Snapshot of session {_sessionCode} from {peer} ({timestamp} UTC)";
        return await backup.UploadAsync(path, blob, msg);
    }

    private string? PromptForCode()
    {
        using var dlg = new Form
        {
            Text = "Join Music Session",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(320, 130),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        var label = new Label { Text = "Session code (from the host):", Location = new Point(16, 16), AutoSize = true };
        var input = new TextBox
        {
            Location = new Point(16, 40),
            Width = 288,
            CharacterCasing = CharacterCasing.Upper
        };
        var ok = new Button { Text = "Join", DialogResult = DialogResult.OK, Location = new Point(148, 86), Width = 76 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(228, 86), Width = 76 };
        dlg.Controls.AddRange(new Control[] { label, input, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }
}
