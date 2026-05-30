using System.Drawing.Drawing2D;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace SlinnerBMusicStudio;

public partial class MainForm : Form
{
    private enum AppState { Idle, Recording, Playing }

    private readonly Settings _settings;
    private readonly Project _project = new();
    private AppState _state = AppState.Idle;

    // A WaveIn that runs the whole time the app is open: it drives the live
    // input-level meter and, while recording, also stores captured samples.
    private WaveInEvent? _monitorIn;
    private WaveOutEvent? _waveOut;        // normal playback
    private WaveOutEvent? _monitorOut;     // existing tracks played back during overdub
    private ClipWaveProvider? _player;

    private readonly List<float> _recBuffer = new();
    private readonly object _recLock = new();
    private int _liveConsumed;
    private volatile float _recPeak;
    private volatile bool _storing;
    private DateTime _recStart;
    private int _recordTrack;

    private float[] _clipboard = Array.Empty<float>();
    private string? _currentPath;
    private bool _dirty;
    private bool _mfReady;
    private bool _populating;

    public MainForm()
    {
        _settings = Settings.Load();
        InitializeComponent();
        SetAppIcon();

        waveform.Project = _project;
        waveform.SelectionChanged += (_, _) => { UpdateUiState(); UpdateStatus(); };
        _project.Changed += (_, _) => { UpdateUiState(); UpdateStatus(); };

        waveform.AllowDrop = true;
        waveform.DragEnter += Waveform_DragOver;
        waveform.DragOver += Waveform_DragOver;
        waveform.DragDrop += Waveform_DragDrop;

        try { MediaFoundationApi.Startup(); _mfReady = true; }
        catch { _mfReady = false; }

        SetupTooltips();
        RestoreWindow();
    }

    private void MainForm_Load(object? sender, EventArgs e)
    {
        PopulateDevices();
        StartMonitor();
        levelTimer.Start();          // the input meter runs continuously
        UpdateUiState();
        UpdateStatus();
        UpdateTitle();
    }

    // --- startup helpers ---------------------------------------------------

    private void SetAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(70, 130, 220));
            using var pen = new Pen(Color.FromArgb(70, 130, 220), 3f);
            g.FillEllipse(brush, 5, 19, 13, 10);
            g.DrawLine(pen, 16, 24, 16, 5);
            g.DrawLine(pen, 16, 5, 26, 11);
        }
        Icon = Icon.FromHandle(bmp.GetHicon());
    }

    private void RestoreWindow()
    {
        if (_settings.WindowWidth is int w && _settings.WindowHeight is int h
            && w >= MinimumSize.Width && h >= MinimumSize.Height)
        {
            Size = new Size(w, h);
        }
        if (_settings.WindowX is int x && _settings.WindowY is int y)
        {
            var rect = new Rectangle(x, y, Width, Height);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect)))
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(x, y);
            }
        }
    }

    private void SetupTooltips()
    {
        toolTip.SetToolTip(recordButton, "Record into a new track (Ctrl+R). Existing tracks play back so you can play along.");
        toolTip.SetToolTip(stopButton, "Stop recording or playback.");
        toolTip.SetToolTip(playButton, "Play the mix of all tracks — selection, or cursor to end (Space).");
        toolTip.SetToolTip(replayButton, "Play the whole mix from the start.");
        toolTip.SetToolTip(addTrackButton, "Add an empty track (Ctrl+T).");
        toolTip.SetToolTip(removeTrackButton, "Remove the selected track.");
        toolTip.SetToolTip(micCombo, "Microphone to record from. The level bar shows its live signal.");
        toolTip.SetToolTip(speakerCombo, "Output device for playback and for monitoring while recording.");
        toolTip.SetToolTip(refreshButton, "Rescan for microphones and speakers.");
        toolTip.SetToolTip(levelBar, "Live microphone input level — moves whenever the app is open.");
        toolTip.SetToolTip(zoomOutButton, "Zoom out.");
        toolTip.SetToolTip(zoomInButton, "Zoom in.");
        toolTip.SetToolTip(zoomFitButton, "Zoom so the whole project fits.");
        toolTip.SetToolTip(undoButton, "Undo the last edit (Ctrl+Z).");
        toolTip.SetToolTip(redoButton, "Redo (Ctrl+Y).");
        toolTip.SetToolTip(cutButton, "Cut the selection from the selected track (Ctrl+X).");
        toolTip.SetToolTip(copyButton, "Copy the selection (Ctrl+C).");
        toolTip.SetToolTip(pasteButton, "Paste at the cursor on the selected track (Ctrl+V).");
        toolTip.SetToolTip(deleteButton, "Delete the selection from the selected track (Delete).");
        toolTip.SetToolTip(trimButton, "Trim the selected track to the selection.");
        toolTip.SetToolTip(silenceButton, "Replace the selection with silence.");
    }

    private void PopulateDevices()
    {
        _populating = true;
        try
        {
            string? prevMic = micCombo.SelectedItem?.ToString() ?? _settings.LastMicName;
            micCombo.Items.Clear();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                try { micCombo.Items.Add(WaveInEvent.GetCapabilities(i).ProductName); }
                catch { micCombo.Items.Add($"Microphone {i + 1}"); }
            }
            if (micCombo.Items.Count > 0)
            {
                int idx = prevMic != null ? micCombo.Items.IndexOf(prevMic) : -1;
                micCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }

            string? prevSpk = speakerCombo.SelectedItem?.ToString() ?? _settings.LastSpeakerName;
            speakerCombo.Items.Clear();
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                try { speakerCombo.Items.Add(WaveOut.GetCapabilities(i).ProductName); }
                catch { speakerCombo.Items.Add($"Speakers {i + 1}"); }
            }
            if (speakerCombo.Items.Count > 0)
            {
                int idx = prevSpk != null ? speakerCombo.Items.IndexOf(prevSpk) : -1;
                speakerCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }
        }
        finally
        {
            _populating = false;
        }
    }

    private int SpeakerDevice() => speakerCombo.SelectedIndex;   // -1 means system default

    // --- microphone monitoring --------------------------------------------

    private void StartMonitor()
    {
        StopMonitor();
        if (micCombo.SelectedIndex < 0) return;
        try
        {
            _monitorIn = new WaveInEvent
            {
                DeviceNumber = micCombo.SelectedIndex,
                WaveFormat = new WaveFormat(Project.Rate, 16, 1),
                BufferMilliseconds = 50
            };
            _monitorIn.DataAvailable += OnMicData;
            _monitorIn.StartRecording();
        }
        catch
        {
            StopMonitor();   // leave the meter at zero; recording will report the error
        }
    }

    private void StopMonitor()
    {
        if (_monitorIn != null)
        {
            _monitorIn.DataAvailable -= OnMicData;
            try { _monitorIn.StopRecording(); } catch { }
            try { _monitorIn.Dispose(); } catch { }
            _monitorIn = null;
        }
        _recPeak = 0f;
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2;
        bool storing = _storing;
        float[]? chunk = storing ? new float[count] : null;
        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float v = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            if (chunk != null) chunk[i] = v;
            float a = Math.Abs(v);
            if (a > peak) peak = a;
        }
        _recPeak = peak;
        if (chunk != null)
            lock (_recLock) { if (_storing) _recBuffer.AddRange(chunk); }
    }

    private void levelTimer_Tick(object? sender, EventArgs e)
    {
        levelBar.Value = Math.Clamp((int)(_recPeak * 100f), 0, 100);

        if (_state != AppState.Recording) return;

        timeLabel.Text = Fmt((DateTime.Now - _recStart).TotalSeconds);

        float[] chunk;
        lock (_recLock)
        {
            int have = _recBuffer.Count;
            if (have <= _liveConsumed) return;
            chunk = new float[have - _liveConsumed];
            _recBuffer.CopyTo(_liveConsumed, chunk, 0, chunk.Length);
            _liveConsumed = have;
        }
        waveform.AppendLive(chunk, chunk.Length);
    }

    // --- recording ---------------------------------------------------------

    private void StartRecording()
    {
        if (_state != AppState.Idle) return;
        if (micCombo.Items.Count == 0 || micCombo.SelectedIndex < 0)
        {
            MessageBox.Show(this, "No microphone was found. Plug one in, then click \"Rescan\".",
                "No microphone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_monitorIn == null)
        {
            StartMonitor();
            if (_monitorIn == null)
            {
                MessageBox.Show(this, "Could not access the microphone.",
                    "Recording error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        try
        {
            float[] backing = _project.Mixdown();   // existing tracks, for overdub monitoring

            _state = AppState.Recording;
            _project.AddTrack($"Track {_project.TrackCount + 1}");
            _recordTrack = _project.TrackCount - 1;
            waveform.ActiveTrack = _recordTrack;
            waveform.BeginLive(_recordTrack);

            lock (_recLock) { _recBuffer.Clear(); }
            _liveConsumed = 0;
            _recStart = DateTime.Now;
            _storing = true;

            if (backing.Length > 0)
            {
                try
                {
                    _monitorOut = new WaveOutEvent { DeviceNumber = SpeakerDevice() };
                    _monitorOut.PlaybackStopped += OnMonitorStopped;
                    _monitorOut.Init(new ClipWaveProvider(backing, Project.Rate, 0, backing.Length));
                    _monitorOut.Play();
                }
                catch
                {
                    CleanupMonitorOut();   // recording still works without the backing playback
                }
            }
        }
        catch (Exception ex)
        {
            _storing = false;
            _state = AppState.Idle;
            CleanupMonitorOut();
            waveform.EndLive();
            MessageBox.Show(this, "Could not start recording:\n\n" + ex.Message,
                "Recording error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        UpdateUiState();
        UpdateStatus();
        UpdateTitle();
    }

    private void StopRecording()
    {
        if (_state != AppState.Recording) return;

        _storing = false;
        CleanupMonitorOut();

        float[] recorded;
        lock (_recLock)
        {
            recorded = _recBuffer.ToArray();
            _recBuffer.Clear();
        }
        _liveConsumed = 0;

        waveform.EndLive();
        _project.SetTrackSamples(_recordTrack, recorded);
        _state = AppState.Idle;
        if (recorded.Length > 0) _dirty = true;

        waveform.ActiveTrack = _recordTrack;
        waveform.ZoomToFit();

        UpdateUiState();
        UpdateStatus();
        UpdateTitle();
    }

    private void OnMonitorStopped(object? sender, StoppedEventArgs e)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => OnMonitorStopped(sender, e))); return; }
        CleanupMonitorOut();
    }

    private void CleanupMonitorOut()
    {
        if (_monitorOut != null)
        {
            _monitorOut.PlaybackStopped -= OnMonitorStopped;
            try { _monitorOut.Stop(); } catch { }
            try { _monitorOut.Dispose(); } catch { }
            _monitorOut = null;
        }
    }

    // --- playback ----------------------------------------------------------

    private void StartPlayback(int from, int to)
    {
        if (_state != AppState.Idle || _project.IsEmpty) return;
        float[] mix = _project.Mixdown();
        from = Math.Clamp(from, 0, mix.Length);
        to = Math.Clamp(to, from, mix.Length);
        if (to - from < 1) return;
        try
        {
            _player = new ClipWaveProvider(mix, Project.Rate, from, to);
            _waveOut = new WaveOutEvent { DeviceNumber = SpeakerDevice() };
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            _waveOut.Init(_player);
            _waveOut.Play();
            _state = AppState.Playing;
            playTimer.Start();
        }
        catch (Exception ex)
        {
            CleanupPlayback();
            _state = AppState.Idle;
            MessageBox.Show(this, "Could not play audio:\n\n" + ex.Message,
                "Playback error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateUiState();
        UpdateStatus();
        UpdateTitle();
    }

    private void StopPlayback()
    {
        if (_state == AppState.Playing)
            try { _waveOut?.Stop(); } catch { }
    }

    private void playTimer_Tick(object? sender, EventArgs e)
    {
        if (_state == AppState.Playing && _player != null)
        {
            waveform.PlayHead = _player.Position;
            timeLabel.Text = Fmt(_player.Position / (double)Project.Rate);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => OnPlaybackStopped(sender, e))); return; }
        playTimer.Stop();
        CleanupPlayback();
        _state = AppState.Idle;
        waveform.PlayHead = -1;
        UpdateUiState();
        UpdateStatus();
        UpdateTitle();
    }

    private void CleanupPlayback()
    {
        if (_waveOut != null)
        {
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
            try { _waveOut.Dispose(); } catch { }
            _waveOut = null;
        }
        _player = null;
    }

    private void TogglePlay()
    {
        if (_state == AppState.Playing) { StopPlayback(); return; }
        if (_state != AppState.Idle || _project.IsEmpty) return;

        int from = waveform.HasSelection ? waveform.SelectionStart : waveform.CursorSample;
        int to = waveform.HasSelection ? waveform.SelectionEnd : _project.Length;
        if (from >= _project.Length) { from = 0; to = _project.Length; }
        StartPlayback(from, to);
    }

    // --- transport buttons -------------------------------------------------

    private void recordButton_Click(object? sender, EventArgs e) => StartRecording();

    private void stopButton_Click(object? sender, EventArgs e)
    {
        if (_state == AppState.Recording) StopRecording();
        else if (_state == AppState.Playing) StopPlayback();
    }

    private void playButton_Click(object? sender, EventArgs e) => TogglePlay();

    private void replayButton_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || _project.IsEmpty) return;
        waveform.SetCursor(0);
        StartPlayback(0, _project.Length);
    }

    private void refreshButton_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle) return;
        PopulateDevices();
        StartMonitor();
    }

    private void micCombo_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (!_populating && _state == AppState.Idle) StartMonitor();
    }

    private void zoomInButton_Click(object? sender, EventArgs e) => waveform.ZoomIn();
    private void zoomOutButton_Click(object? sender, EventArgs e) => waveform.ZoomOut();
    private void zoomFitButton_Click(object? sender, EventArgs e) => waveform.ZoomToFit();

    // --- tracks ------------------------------------------------------------

    private void AddTrack_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle) return;
        _project.AddTrack($"Track {_project.TrackCount + 1}");
        waveform.ActiveTrack = _project.TrackCount - 1;
    }

    private void RemoveTrack_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || _project.TrackCount == 0) return;
        int t = waveform.ActiveTrack;
        if (t < 0 || t >= _project.TrackCount) return;

        var track = _project.Tracks[t];
        if (track.Length > 0)
        {
            var r = MessageBox.Show(this, $"Remove \"{track.Name}\" and its audio?",
                "Remove track", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            _dirty = true;
        }
        _project.RemoveTrack(t);
        waveform.ActiveTrack = Math.Min(t, _project.TrackCount - 1);
    }

    // --- editing (operates on the selected track) --------------------------

    private int ActiveTrack => waveform.ActiveTrack;

    private bool ActiveValid =>
        _project.TrackCount > 0 && ActiveTrack >= 0 && ActiveTrack < _project.TrackCount;

    private bool ActiveHasAudio => ActiveValid && _project.Tracks[ActiveTrack].Length > 0;

    private (int start, int end) Selection() => (waveform.SelectionStart, waveform.SelectionEnd);

    private (int start, int end) EffectRange()
    {
        if (waveform.HasSelection) return (waveform.SelectionStart, waveform.SelectionEnd);
        return (0, ActiveValid ? _project.Tracks[ActiveTrack].Length : 0);
    }

    private void Undo_Click(object? sender, EventArgs e)
    {
        if (_state == AppState.Idle && _project.CanUndo) { _project.Undo(); _dirty = true; }
    }

    private void Redo_Click(object? sender, EventArgs e)
    {
        if (_state == AppState.Idle && _project.CanRedo) { _project.Redo(); _dirty = true; }
    }

    private void Cut_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !waveform.HasSelection || !ActiveValid) return;
        var (s, en) = Selection();
        _clipboard = _project.Extract(ActiveTrack, s, en);
        _project.Delete(ActiveTrack, s, en);
        waveform.SetCursor(s);
        _dirty = true;
    }

    private void Copy_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !waveform.HasSelection || !ActiveValid) return;
        var (s, en) = Selection();
        _clipboard = _project.Extract(ActiveTrack, s, en);
        UpdateUiState();
    }

    private void Paste_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || _clipboard.Length == 0 || !ActiveValid) return;
        int at = waveform.CursorSample;
        _project.Insert(ActiveTrack, at, _clipboard);
        waveform.SetSelection(at, at + _clipboard.Length);
        _dirty = true;
    }

    private void Delete_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !waveform.HasSelection || !ActiveValid) return;
        var (s, en) = Selection();
        _project.Delete(ActiveTrack, s, en);
        waveform.SetCursor(s);
        _dirty = true;
    }

    private void SelectAll_Click(object? sender, EventArgs e)
    {
        if (_state == AppState.Idle) waveform.SelectAll();
    }

    private void Trim_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !waveform.HasSelection || !ActiveValid) return;
        var (s, en) = Selection();
        _project.Trim(ActiveTrack, s, en);
        waveform.SetSelection(0, en - s);
        waveform.ZoomToFit();
        _dirty = true;
    }

    private void Silence_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !waveform.HasSelection || !ActiveValid) return;
        var (s, en) = Selection();
        _project.Silence(ActiveTrack, s, en);
        _dirty = true;
    }

    // --- effects -----------------------------------------------------------

    private void Normalize_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !ActiveHasAudio) return;
        var (s, en) = EffectRange();
        _project.Normalize(ActiveTrack, s, en);
        _dirty = true;
    }

    private void FadeIn_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !ActiveHasAudio) return;
        var (s, en) = EffectRange();
        _project.FadeIn(ActiveTrack, s, en);
        _dirty = true;
    }

    private void FadeOut_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !ActiveHasAudio) return;
        var (s, en) = EffectRange();
        _project.FadeOut(ActiveTrack, s, en);
        _dirty = true;
    }

    private void LoudnessNormalize_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !ActiveHasAudio) return;

        using var dlg = new Form
        {
            Text = "Make Louder",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 168),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        var info = new Label
        {
            Text = "Target loudness (RMS dB).  Higher = louder.",
            Location = new Point(16, 12),
            AutoSize = true
        };
        var preset = new ComboBox
        {
            Location = new Point(16, 36),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        preset.Items.AddRange(new object[]
        {
            "Subtle  (-20 dB)",
            "Normal  (-16 dB)",
            "Loud  (-14 dB)",
            "Very loud  (-12 dB)",
            "Maximum  (-9 dB)"
        });
        preset.SelectedIndex = 2;

        var customLabel = new Label
        {
            Text = "Custom target:",
            Location = new Point(16, 72),
            AutoSize = true
        };
        var input = new NumericUpDown
        {
            Location = new Point(120, 70),
            Width = 96,
            Minimum = -36,
            Maximum = -3,
            DecimalPlaces = 1,
            Increment = 0.5M,
            Value = -14
        };
        preset.SelectedIndexChanged += (_, _) =>
        {
            decimal v = preset.SelectedIndex switch
            {
                0 => -20m, 1 => -16m, 2 => -14m, 3 => -12m, 4 => -9m,
                _ => -14m
            };
            input.Value = v;
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(184, 124), Width = 76 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(266, 124), Width = 76 };
        dlg.Controls.AddRange(new Control[] { info, preset, customLabel, input, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var (s, en) = EffectRange();
        _project.LoudnessNormalize(ActiveTrack, s, en, (float)input.Value);
        _dirty = true;
    }

    private void Amplify_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !ActiveHasAudio) return;

        using var dlg = new Form
        {
            Text = "Amplify",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(286, 112),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        var label = new Label { Text = "Gain (decibels):", Location = new Point(16, 20), AutoSize = true };
        var input = new NumericUpDown
        {
            Location = new Point(152, 17),
            Width = 116,
            Minimum = -36,
            Maximum = 36,
            DecimalPlaces = 1,
            Increment = 0.5M,
            Value = 6
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(110, 68), Width = 76 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(192, 68), Width = 76 };
        dlg.Controls.AddRange(new Control[] { label, input, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        float factor = (float)Math.Pow(10.0, (double)input.Value / 20.0);
        var (s, en) = EffectRange();
        _project.Amplify(ActiveTrack, s, en, factor);
        _dirty = true;
    }

    // --- files -------------------------------------------------------------

    private void New_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !ConfirmDiscard()) return;
        _project.Clear();
        _currentPath = null;
        _dirty = false;
        waveform.SetCursor(0);
        waveform.ZoomToFit();
        UpdateTitle();
    }

    private void Open_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || !ConfirmDiscard()) return;

        using var ofd = new OpenFileDialog
        {
            Title = "Open audio file",
            Filter = "Audio files (*.wav;*.mp3)|*.wav;*.mp3|WAV audio (*.wav)|*.wav|"
                   + "MP3 audio (*.mp3)|*.mp3|All files (*.*)|*.*"
        };
        if (_settings.LastFolder != null && Directory.Exists(_settings.LastFolder))
            ofd.InitialDirectory = _settings.LastFolder;
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            float[] samples = LoadAudioFile(ofd.FileName);
            _project.LoadSingle(samples, Path.GetFileNameWithoutExtension(ofd.FileName));
            _currentPath = ofd.FileName;
            _settings.LastFolder = Path.GetDirectoryName(ofd.FileName);
            _dirty = false;
            waveform.ActiveTrack = 0;
            waveform.SetCursor(0);
            waveform.ZoomToFit();
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open that file:\n\n" + ex.Message,
                "Open error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void ImportTrack_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle) return;
        using var ofd = new OpenFileDialog
        {
            Title = "Import audio as new track(s)",
            Filter = "Audio files (*.wav;*.mp3)|*.wav;*.mp3|All files (*.*)|*.*",
            Multiselect = true
        };
        if (_settings.LastFolder != null && Directory.Exists(_settings.LastFolder))
            ofd.InitialDirectory = _settings.LastFolder;
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        ImportFiles(ofd.FileNames);
    }

    private void Waveform_DragOver(object? sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
        if (_state != AppState.Idle || e.Data == null) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsAudioFile))
            e.Effect = DragDropEffects.Copy;
    }

    private void Waveform_DragDrop(object? sender, DragEventArgs e)
    {
        if (_state != AppState.Idle || e.Data == null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var audio = files.Where(IsAudioFile).ToArray();
        if (audio.Length > 0) ImportFiles(audio);
    }

    /// <summary>Loads each file (downmixed to mono, 44.1 kHz) and appends it as a new track.</summary>
    private void ImportFiles(string[] paths)
    {
        if (_state != AppState.Idle || paths.Length == 0) return;

        int loaded = 0;
        var errors = new List<string>();
        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (var path in paths)
            {
                try
                {
                    float[] samples = LoadAudioFile(path);
                    _project.AddTrack(Path.GetFileNameWithoutExtension(path), samples);
                    loaded++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(path)} — {ex.Message}");
                }
            }
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        if (loaded > 0)
        {
            _dirty = true;
            waveform.ActiveTrack = _project.TrackCount - 1;
            waveform.ZoomToFit();
        }
        if (errors.Count > 0)
        {
            MessageBox.Show(this, "These files could not be imported:\n\n" + string.Join("\n", errors),
                "Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveWav_Click(object? sender, EventArgs e) => SaveWavInteractive();

    private bool SaveWavInteractive()
    {
        if (_project.IsEmpty) return true;

        using var sfd = new SaveFileDialog
        {
            Title = "Save mix as WAV",
            Filter = "WAV audio (*.wav)|*.wav",
            FileName = SuggestName("wav")
        };
        if (_settings.LastFolder != null && Directory.Exists(_settings.LastFolder))
            sfd.InitialDirectory = _settings.LastFolder;
        if (sfd.ShowDialog(this) != DialogResult.OK) return false;

        try
        {
            Cursor = Cursors.WaitCursor;
            float[] mix = _project.Mixdown();
            using (var writer = new WaveFileWriter(sfd.FileName, new WaveFormat(Project.Rate, 16, 1)))
            {
                var provider = new ClipWaveProvider(mix, Project.Rate, 0, mix.Length);
                var buffer = new byte[16384];
                int read;
                while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
                    writer.Write(buffer, 0, read);
            }
            _currentPath = sfd.FileName;
            _settings.LastFolder = Path.GetDirectoryName(sfd.FileName);
            _dirty = false;
            UpdateTitle();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the file:\n\n" + ex.Message,
                "Save error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void ExportMp3_Click(object? sender, EventArgs e)
    {
        if (_state != AppState.Idle || _project.IsEmpty) return;
        if (!_mfReady)
        {
            MessageBox.Show(this, "The Windows MP3 encoder is not available on this PC.\nUse \"Save Mix as WAV\" instead.",
                "MP3 export unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = "Export mix as MP3",
            Filter = "MP3 audio (*.mp3)|*.mp3",
            FileName = SuggestName("mp3")
        };
        if (_settings.LastFolder != null && Directory.Exists(_settings.LastFolder))
            sfd.InitialDirectory = _settings.LastFolder;
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            float[] mix = _project.Mixdown();
            var provider = new ClipWaveProvider(mix, Project.Rate, 0, mix.Length);
            MediaFoundationEncoder.EncodeToMp3(provider, sfd.FileName, 192000);
            _settings.LastFolder = Path.GetDirectoryName(sfd.FileName);
            MessageBox.Show(this, "Exported to:\n" + sfd.FileName, "MP3 export",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not export MP3:\n\n" + ex.Message,
                "Export error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void Exit_Click(object? sender, EventArgs e) => Close();

    private static readonly string[] AudioExtensions =
        { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".flac" };

    private static bool IsAudioFile(string path)
        => AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static float[] LoadAudioFile(string path)
    {
        using var reader = new AudioFileReader(path);
        int channels = Math.Max(1, reader.WaveFormat.Channels);
        int rate = reader.WaveFormat.SampleRate;

        var mono = new List<float>();
        var buffer = new float[channels * 8192];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i + channels <= read; i += channels)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++) sum += buffer[i + c];
                mono.Add(sum / channels);
            }
        }

        var samples = mono.ToArray();
        return rate == Project.Rate ? samples : Resample(samples, rate, Project.Rate);
    }

    private static float[] Resample(float[] src, int fromRate, int toRate)
    {
        if (fromRate == toRate || src.Length == 0) return src;
        double ratio = (double)toRate / fromRate;
        int n = Math.Max(1, (int)(src.Length * ratio));
        var dst = new float[n];
        for (int i = 0; i < n; i++)
        {
            double pos = i / ratio;
            int i0 = (int)pos;
            double frac = pos - i0;
            float a = src[Math.Min(i0, src.Length - 1)];
            float b = src[Math.Min(i0 + 1, src.Length - 1)];
            dst[i] = (float)(a + (b - a) * frac);
        }
        return dst;
    }

    private string SuggestName(string extension)
        => _currentPath != null
            ? Path.GetFileNameWithoutExtension(_currentPath) + "." + extension
            : $"recording-{DateTime.Now:yyyy-MM-dd-HHmmss}.{extension}";

    private bool ConfirmDiscard()
    {
        if (!_dirty || _project.IsEmpty) return true;
        var result = MessageBox.Show(this, "This project has unsaved changes. Save the mix now?",
            "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        return result switch
        {
            DialogResult.Yes => SaveWavInteractive(),
            DialogResult.No => true,
            _ => false
        };
    }

    // --- help --------------------------------------------------------------

    private void Shortcuts_Click(object? sender, EventArgs e)
    {
        MessageBox.Show(this,
            "Space  Play / Stop\n" +
            "Ctrl+R  Record (into a new track) / Stop recording\n" +
            "Ctrl+T  Add track\n\n" +
            "Ctrl+Z  Undo  Ctrl+Y  Redo\n" +
            "Ctrl+X  Cut  Ctrl+C  Copy  Ctrl+V  Paste\n" +
            "Delete  Delete selection  Ctrl+A  Select all\n\n" +
            "Ctrl+N  New  Ctrl+O  Open  Ctrl+S  Save mix as WAV\n\n" +
            "Click a track to select it for editing; drag in it to select a range.\n" +
            "Drag an audio file onto the waveform to import it as a new track.\n" +
            "Mouse wheel scrolls tracks; Shift+wheel scrolls time; Ctrl+wheel zooms.",
            "Keyboard shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async void CheckForUpdates_Click(object? sender, EventArgs e)
    {
        await Updater.CheckAsync(this, showWhenNoUpdate: true);
    }

    private void About_Click(object? sender, EventArgs e)
    {
        MessageBox.Show(this,
            $"SlinnerB's Music Studio\nVersion {Updater.CurrentVersion}\n\n" +
            "A portable multi-track microphone recorder and audio editor.\n" +
            "Record new tracks while existing ones play back, edit each track,\n" +
            "and save the mix. Mono, 44.1 kHz.\n\n" +
            "Built with .NET 8 and NAudio.",
            "About SlinnerB's Music Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // --- UI state ----------------------------------------------------------

    private void UpdateUiState()
    {
        bool idle = _state == AppState.Idle;
        bool recording = _state == AppState.Recording;
        bool playing = _state == AppState.Playing;
        bool has = !_project.IsEmpty;
        bool anyTracks = _project.TrackCount > 0;
        bool sel = waveform.HasSelection;
        bool clip = _clipboard.Length > 0;
        bool activeValid = ActiveValid;
        bool activeAudio = ActiveHasAudio;

        recordButton.Enabled = idle && micCombo.Items.Count > 0;
        stopButton.Enabled = recording || playing;
        playButton.Enabled = idle && has;
        replayButton.Enabled = idle && has;
        addTrackButton.Enabled = idle;
        removeTrackButton.Enabled = miRemoveTrack.Enabled = idle && anyTracks;
        micCombo.Enabled = idle;
        speakerCombo.Enabled = idle;
        refreshButton.Enabled = idle;
        zoomInButton.Enabled = zoomOutButton.Enabled = zoomFitButton.Enabled = has && !recording;

        undoButton.Enabled = miUndo.Enabled = idle && _project.CanUndo;
        redoButton.Enabled = miRedo.Enabled = idle && _project.CanRedo;
        cutButton.Enabled = miCut.Enabled = idle && sel && activeAudio;
        copyButton.Enabled = miCopy.Enabled = idle && sel && activeAudio;
        pasteButton.Enabled = miPaste.Enabled = idle && clip && activeValid;
        deleteButton.Enabled = miDelete.Enabled = idle && sel && activeAudio;
        trimButton.Enabled = miTrim.Enabled = idle && sel && activeAudio;
        silenceButton.Enabled = miSilence.Enabled = idle && sel && activeAudio;

        miSelectAll.Enabled = idle && has;
        miSave.Enabled = idle && has;
        miExport.Enabled = idle && has;
        miNormalize.Enabled = idle && activeAudio;
        miAmplify.Enabled = idle && activeAudio;
        miFadeIn.Enabled = idle && activeAudio;
        miFadeOut.Enabled = idle && activeAudio;
    }

    private void UpdateStatus()
    {
        if (_state == AppState.Recording)
        {
            string into = ActiveValid ? _project.Tracks[_recordTrack].Name : "a new track";
            statusLabel.Text = $"● Recording into {into} — existing tracks are playing for reference.";
            return;
        }
        if (_state == AppState.Playing)
        {
            statusLabel.Text = "▶ Playing the mix…";
            return;
        }
        if (_project.TrackCount == 0)
        {
            statusLabel.Text = "No tracks yet — press ● Record, use + Add Track, or open a file.";
            timeLabel.Text = "0:00.000";
            return;
        }

        double rate = Project.Rate;
        string active = ActiveValid ? _project.Tracks[ActiveTrack].Name : "—";
        string text = $"Selected: {active}      Mix length {Fmt(_project.Duration)}      "
                    + $"Cursor {Fmt(waveform.CursorSample / rate)}";
        if (waveform.HasSelection)
            text += $"      Selection {Fmt((waveform.SelectionEnd - waveform.SelectionStart) / rate)}";
        statusLabel.Text = text;
        timeLabel.Text = Fmt(waveform.CursorSample / rate);
    }

    private void UpdateTitle()
    {
        string name = _currentPath != null ? Path.GetFileName(_currentPath) : "Untitled";
        string mark = _dirty ? "*" : "";
        string suffix = _state switch
        {
            AppState.Recording => "  [Recording]",
            AppState.Playing => "  [Playing]",
            _ => ""
        };
        string session = InSession ? $"  [Session {_sessionCode} · v{_sessionVersion}]" : "";
        Text = $"{mark}{name} — SlinnerB's Music Studio{session}{suffix}";
    }

    private static string Fmt(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        int minutes = (int)(seconds / 60);
        double rest = seconds - minutes * 60;
        return $"{minutes}:{rest:00.000}";
    }

    // --- keyboard & shutdown ----------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Space && ActiveControl is not ComboBox)
        {
            if (_state != AppState.Recording) TogglePlay();
            return true;
        }
        if (keyData == (Keys.Control | Keys.R))
        {
            if (_state == AppState.Idle) StartRecording();
            else if (_state == AppState.Recording) StopRecording();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_state == AppState.Recording) { _storing = false; CleanupMonitorOut(); }
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        levelTimer.Stop();
        playTimer.Stop();
        CleanupMonitorOut();
        CleanupPlayback();
        StopMonitor();
        StopHostIfRunning();
        _state = AppState.Idle;
        if (_mfReady)
            try { MediaFoundationApi.Shutdown(); } catch { }

        if (micCombo.SelectedItem != null) _settings.LastMicName = micCombo.SelectedItem.ToString();
        if (speakerCombo.SelectedItem != null) _settings.LastSpeakerName = speakerCombo.SelectedItem.ToString();
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        _settings.Save();
    }
}
