namespace SlinnerBMusicStudio;

/// <summary>
/// Scrollable, zoomable multi-track waveform display. Tracks are stacked as
/// horizontal lanes, each with a header (name, duration, mute toggle). Click a
/// lane to make it the active track for editing; drag in the wave area to
/// select a time range. During recording one lane shows live captured audio.
/// </summary>
public class WaveformView : Control
{
    private const int Rate = Project.Rate;
    private const int RulerHeight = 22;
    private const int HeaderWidth = 150;
    private const int TrackHeight = 104;
    private const int TrackGap = 6;
    private const int LaneStride = TrackHeight + TrackGap;
    private const int PeakBucket = 256;
    private const int LiveBucket = 368;          // ~120 buckets per second

    private Project? _project;
    private readonly HScrollBar _hScroll;
    private readonly VScrollBar _vScroll;
    private readonly Font _smallFont;
    private readonly Font _nameFont;

    private double _samplesPerPixel = 1000;
    private int _offset;                          // leftmost visible sample

    private int _selStart, _selEnd;               // time selection, in samples
    private int _activeTrack;
    private bool _dragging;
    private int _dragAnchor;
    private int _playHead = -1;

    // Move mode: drag a lane to slide its clip along the timeline.
    private bool _movingClip;
    private int _moveTrack;
    private int _moveAnchorSample;
    private int _moveOrigOffset;

    // Per-track peak caches (rebuilt only when a track's buffer reference changes).
    private readonly List<float[]> _peakMin = new();
    private readonly List<float[]> _peakMax = new();
    private readonly List<float[]?> _peakSrc = new();

    // Live recording lane.
    private int _liveTrack = -1;
    private readonly List<float> _liveMin = new();
    private readonly List<float> _liveMax = new();
    private int _liveFill;
    private float _liveCurMin = 1f, _liveCurMax = -1f;

    public event EventHandler? SelectionChanged;
    public event EventHandler? TrackMoved;

    /// <summary>When true, dragging a lane slides its clip instead of selecting a time range.</summary>
    public bool MoveMode { get; set; }

    public WaveformView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(28, 30, 36);
        _smallFont = new Font("Segoe UI", 7.5f);
        _nameFont = new Font("Segoe UI", 9f, FontStyle.Bold);

        _hScroll = new HScrollBar { Dock = DockStyle.Bottom, Visible = false };
        _hScroll.ValueChanged += (_, _) => { _offset = _hScroll.Value; Invalidate(); };
        _vScroll = new VScrollBar { Dock = DockStyle.Right, Visible = false };
        _vScroll.ValueChanged += (_, _) => Invalidate();
        Controls.Add(_hScroll);
        Controls.Add(_vScroll);
    }

    public Project? Project
    {
        get => _project;
        set
        {
            if (_project != null) _project.Changed -= OnProjectChanged;
            _project = value;
            if (_project != null) _project.Changed += OnProjectChanged;
            OnProjectChanged(this, EventArgs.Empty);
        }
    }

    // --- public state ------------------------------------------------------

    public int SelectionStart => _selStart;
    public int SelectionEnd => _selEnd;
    public bool HasSelection => _selEnd > _selStart;
    public int CursorSample => _selStart;

    public int ActiveTrack
    {
        get => _activeTrack;
        set
        {
            _activeTrack = Math.Clamp(value, 0, Math.Max(0, (_project?.TrackCount ?? 1) - 1));
            EnsureTrackVisible(_activeTrack);
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int PlayHead
    {
        get => _playHead;
        set { _playHead = value; if (value >= 0) EnsureVisible(value); Invalidate(); }
    }

    public void SetSelection(int start, int end)
    {
        int len = _project?.Length ?? 0;
        _selStart = Math.Clamp(Math.Min(start, end), 0, len);
        _selEnd = Math.Clamp(Math.Max(start, end), 0, len);
        EnsureVisible(_selStart);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCursor(int pos) => SetSelection(pos, pos);
    public void SelectAll() => SetSelection(0, _project?.Length ?? 0);

    // --- live recording lane ----------------------------------------------

    public void BeginLive(int trackIndex)
    {
        _liveTrack = trackIndex;
        _liveMin.Clear();
        _liveMax.Clear();
        _liveFill = 0;
        _liveCurMin = 1f;
        _liveCurMax = -1f;
        EnsureTrackVisible(trackIndex);
        Invalidate();
    }

    public void AppendLive(float[] data, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float v = data[i];
            if (v < _liveCurMin) _liveCurMin = v;
            if (v > _liveCurMax) _liveCurMax = v;
            if (++_liveFill >= LiveBucket)
            {
                _liveMin.Add(_liveCurMin);
                _liveMax.Add(_liveCurMax);
                _liveFill = 0;
                _liveCurMin = 1f;
                _liveCurMax = -1f;
            }
        }
        Invalidate();
    }

    public void EndLive()
    {
        _liveTrack = -1;
        Invalidate();
    }

    // --- zoom --------------------------------------------------------------

    public void ZoomToFit()
    {
        _samplesPerPixel = FitSamplesPerPixel();
        _offset = 0;
        UpdateScrollBars();
        Invalidate();
    }

    public void ZoomIn() => SetZoom(_samplesPerPixel / 1.6);
    public void ZoomOut() => SetZoom(_samplesPerPixel * 1.6);

    private void SetZoom(double spp)
    {
        double fit = FitSamplesPerPixel();
        spp = Math.Clamp(spp, Math.Min(1.0 / 64, fit), fit);
        double cursorX = SampleToX(_selStart);
        _samplesPerPixel = spp;
        _offset = _selStart - (int)((cursorX - HeaderWidth) * spp);
        ClampOffset();
        UpdateScrollBars();
        Invalidate();
    }

    private double FitSamplesPerPixel()
    {
        int len = _project?.Length ?? 0;
        if (len < 1) return 1000;
        return Math.Max(1.0 / 64, (double)len / WaveWidth);
    }

    // --- layout helpers ----------------------------------------------------

    private int VScrollW => _vScroll.Visible ? _vScroll.Width : 0;
    private int HScrollH => _hScroll.Visible ? _hScroll.Height : 0;
    private int ContentRight => Width - VScrollW;
    private int ContentBottom => Height - HScrollH;
    private int WaveWidth => Math.Max(1, ContentRight - HeaderWidth);
    private int TracksAreaHeight => Math.Max(1, ContentBottom - RulerHeight);
    private int LaneTop(int i) => RulerHeight + i * LaneStride - _vScroll.Value;

    private int XToSample(int x) => _offset + (int)Math.Round((x - HeaderWidth) * _samplesPerPixel);
    private double SampleToX(int s) => HeaderWidth + (s - _offset) / _samplesPerPixel;
    private int ClampSample(int s) => Math.Clamp(s, 0, _project?.Length ?? 0);

    private void ClampOffset()
    {
        int len = _project?.Length ?? 0;
        int visible = (int)(WaveWidth * _samplesPerPixel);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, len - visible));
    }

    private void EnsureVisible(int sample)
    {
        if (_project == null) return;
        int visible = (int)(WaveWidth * _samplesPerPixel);
        if (sample < _offset) _offset = sample;
        else if (sample > _offset + visible) _offset = Math.Max(0, sample - visible);
        ClampOffset();
        UpdateScrollBars();
    }

    private void EnsureTrackVisible(int i)
    {
        int top = RulerHeight + i * LaneStride;
        int bottom = top + TrackHeight;
        int viewTop = RulerHeight + _vScroll.Value;
        int viewBottom = viewTop + TracksAreaHeight;
        if (top < viewTop) SetVScroll(top - RulerHeight);
        else if (bottom > viewBottom) SetVScroll(bottom - RulerHeight - TracksAreaHeight);
    }

    private void SetVScroll(int value)
    {
        int total = (_project?.TrackCount ?? 0) * LaneStride;
        int max = Math.Max(0, total - TracksAreaHeight);
        value = Math.Clamp(value, 0, max);
        if (_vScroll.Visible) _vScroll.Value = Math.Min(value, _vScroll.Maximum);
    }

    private void UpdateScrollBars()
    {
        int trackCount = _project?.TrackCount ?? 0;
        int total = trackCount * LaneStride;

        if (total > TracksAreaHeight + HScrollH)
        {
            if (!_vScroll.Visible) _vScroll.Visible = true;
            _vScroll.Minimum = 0;
            _vScroll.Maximum = total;
            _vScroll.LargeChange = TracksAreaHeight;
            _vScroll.SmallChange = LaneStride;
            _vScroll.Value = Math.Clamp(_vScroll.Value, 0, Math.Max(0, total - TracksAreaHeight));
        }
        else
        {
            if (_vScroll.Visible) { _vScroll.Value = 0; _vScroll.Visible = false; }
        }

        int len = _project?.Length ?? 0;
        int visible = (int)(WaveWidth * _samplesPerPixel);
        if (_liveTrack < 0 && len > 0 && visible < len)
        {
            if (!_hScroll.Visible) _hScroll.Visible = true;
            ClampOffset();
            _hScroll.Minimum = 0;
            _hScroll.Maximum = len;
            _hScroll.LargeChange = visible;
            _hScroll.SmallChange = Math.Max(1, visible / 10);
            _hScroll.Value = Math.Clamp(_offset, 0, len);
        }
        else
        {
            _offset = Math.Max(0, _offset);
            if (_hScroll.Visible) _hScroll.Visible = false;
        }
    }

    private void OnProjectChanged(object? sender, EventArgs e)
    {
        SyncPeakCaches();
        int len = _project?.Length ?? 0;
        _selStart = Math.Clamp(_selStart, 0, len);
        _selEnd = Math.Clamp(_selEnd, 0, len);
        _activeTrack = Math.Clamp(_activeTrack, 0, Math.Max(0, (_project?.TrackCount ?? 1) - 1));
        if (_samplesPerPixel > FitSamplesPerPixel()) _samplesPerPixel = FitSamplesPerPixel();
        ClampOffset();
        UpdateScrollBars();
        Invalidate();
    }

    private void SyncPeakCaches()
    {
        int n = _project?.TrackCount ?? 0;
        while (_peakMin.Count < n)
        {
            _peakMin.Add(Array.Empty<float>());
            _peakMax.Add(Array.Empty<float>());
            _peakSrc.Add(null);
        }
        while (_peakMin.Count > n)
        {
            _peakMin.RemoveAt(_peakMin.Count - 1);
            _peakMax.RemoveAt(_peakMax.Count - 1);
            _peakSrc.RemoveAt(_peakSrc.Count - 1);
        }
        for (int i = 0; i < n; i++)
        {
            var src = _project!.Tracks[i].Samples;
            if (!ReferenceEquals(_peakSrc[i], src))
                RebuildPeak(i, src);
        }
    }

    private void RebuildPeak(int i, float[] src)
    {
        if (src.Length == 0)
        {
            _peakMin[i] = Array.Empty<float>();
            _peakMax[i] = Array.Empty<float>();
        }
        else
        {
            int buckets = (src.Length + PeakBucket - 1) / PeakBucket;
            var mn = new float[buckets];
            var mx = new float[buckets];
            for (int b = 0; b < buckets; b++)
            {
                int i0 = b * PeakBucket;
                int i1 = Math.Min(src.Length, i0 + PeakBucket);
                float lo = 1f, hi = -1f;
                for (int k = i0; k < i1; k++)
                {
                    if (src[k] < lo) lo = src[k];
                    if (src[k] > hi) hi = src[k];
                }
                mn[b] = lo;
                mx[b] = hi;
            }
            _peakMin[i] = mn;
            _peakMax[i] = mx;
        }
        _peakSrc[i] = src;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ClampOffset();
        UpdateScrollBars();
        Invalidate();
    }

    // --- mouse -------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _project == null || _project.TrackCount == 0) return;
        Focus();

        if (e.Y < RulerHeight)
        {
            if (e.X >= HeaderWidth)
            {
                int s = ClampSample(XToSample(e.X));
                _selStart = _selEnd = s;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        int rel = e.Y - RulerHeight + _vScroll.Value;
        int lane = rel / LaneStride;
        if (lane < 0 || lane >= _project.TrackCount || rel % LaneStride >= TrackHeight) return;

        _activeTrack = lane;

        if (e.X < HeaderWidth)
        {
            if (MuteBoxRect(LaneTop(lane)).Contains(e.X, e.Y) && _liveTrack < 0)
                _project.SetMuted(lane, !_project.Tracks[lane].Muted);
        }
        else if (_liveTrack < 0 && MoveMode)
        {
            _movingClip = true;
            _moveTrack = lane;
            _moveAnchorSample = XToSample(e.X);
            _moveOrigOffset = _project.Tracks[lane].Offset;
            Cursor = Cursors.SizeWE;
        }
        else if (_liveTrack < 0)
        {
            _dragging = true;
            _dragAnchor = ClampSample(XToSample(e.X));
            _selStart = _selEnd = _dragAnchor;
        }

        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_movingClip && _project != null)
        {
            int delta = XToSample(e.X) - _moveAnchorSample;
            int newOff = Math.Max(0, _moveOrigOffset + delta);
            int snap = (int)(_samplesPerPixel * 5);     // snap to the start when close
            if (newOff < snap) newOff = 0;
            _project.SetOffset(_moveTrack, newOff);
            UpdateScrollBars();
            Invalidate();
            return;
        }

        if (!_dragging) return;
        int s = ClampSample(XToSample(e.X));
        _selStart = Math.Min(_dragAnchor, s);
        _selEnd = Math.Max(_dragAnchor, s);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_movingClip)
        {
            _movingClip = false;
            Cursor = Cursors.Default;
            if (_project != null && _project.Tracks[_moveTrack].Offset != _moveOrigOffset)
            {
                _project.CommitMove(_moveTrack, _moveOrigOffset);
                TrackMoved?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_project == null) return;
        if (ModifierKeys.HasFlag(Keys.Control))
        {
            if (e.Delta > 0) ZoomIn(); else ZoomOut();
        }
        else if (ModifierKeys.HasFlag(Keys.Shift) && _hScroll.Visible)
        {
            int step = (int)(WaveWidth * _samplesPerPixel * 0.15);
            _offset = Math.Clamp(_offset - Math.Sign(e.Delta) * step, 0, _hScroll.Maximum);
            UpdateScrollBars();
            Invalidate();
        }
        else if (_vScroll.Visible)
        {
            SetVScroll(_vScroll.Value - Math.Sign(e.Delta) * LaneStride);
        }
        else if (_hScroll.Visible)
        {
            int step = (int)(WaveWidth * _samplesPerPixel * 0.15);
            _offset = Math.Clamp(_offset - Math.Sign(e.Delta) * step, 0, _hScroll.Maximum);
            UpdateScrollBars();
            Invalidate();
        }
    }

    private static Rectangle MuteBoxRect(int laneTop)
        => new Rectangle(14, laneTop + TrackHeight - 34, 64, 22);

    // --- painting ----------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        using (var corner = new SolidBrush(Color.FromArgb(34, 36, 44)))
            g.FillRectangle(corner, 0, 0, HeaderWidth, RulerHeight);
        using (var rulerBg = new SolidBrush(Color.FromArgb(40, 42, 50)))
            g.FillRectangle(rulerBg, HeaderWidth, 0, ContentRight - HeaderWidth, RulerHeight);

        int n = _project?.TrackCount ?? 0;
        if (n == 0)
        {
            DrawHint(g);
            return;
        }

        DrawRuler(g);

        var clip = new Rectangle(0, RulerHeight, Width, Math.Max(1, ContentBottom - RulerHeight));
        g.SetClip(clip);
        for (int i = 0; i < n; i++)
        {
            int ty = LaneTop(i);
            if (ty + TrackHeight < RulerHeight || ty > ContentBottom) continue;
            DrawTrack(g, i, ty);
        }
        g.ResetClip();

        DrawOverlay(g);
    }

    private void DrawTrack(Graphics g, int i, int ty)
    {
        var track = _project!.Tracks[i];
        bool active = i == _activeTrack;

        // Header.
        using (var headBg = new SolidBrush(active ? Color.FromArgb(58, 74, 104) : Color.FromArgb(44, 46, 56)))
            g.FillRectangle(headBg, 0, ty, HeaderWidth, TrackHeight);
        if (active)
            using (var accent = new SolidBrush(Color.FromArgb(90, 150, 240)))
                g.FillRectangle(accent, 0, ty, 4, TrackHeight);

        using (var nameBrush = new SolidBrush(Color.FromArgb(220, 222, 230)))
            g.DrawString(track.Name, _nameFont, nameBrush, 14, ty + 10);
        using (var subBrush = new SolidBrush(Color.FromArgb(150, 154, 166)))
        {
            string sub = FormatTime(track.Length / (double)Rate);
            if (track.Offset > 0) sub += "  @ " + FormatTime(track.Offset / (double)Rate);
            g.DrawString(sub, _smallFont, subBrush, 14, ty + 32);
        }

        var mute = MuteBoxRect(ty);
        if (track.Muted)
        {
            using var mb = new SolidBrush(Color.FromArgb(180, 70, 70));
            g.FillRectangle(mb, mute);
            using var mt = new SolidBrush(Color.White);
            g.DrawString("Muted", _smallFont, mt, mute.X + 12, mute.Y + 4);
        }
        else
        {
            using var mp = new Pen(Color.FromArgb(120, 124, 138));
            g.DrawRectangle(mp, mute);
            using var mt = new SolidBrush(Color.FromArgb(190, 194, 204));
            g.DrawString("Mute", _smallFont, mt, mute.X + 16, mute.Y + 4);
        }

        // Wave area.
        int wx = HeaderWidth;
        int ww = ContentRight - HeaderWidth;
        using (var waveBg = new SolidBrush(Color.FromArgb(32, 34, 42)))
            g.FillRectangle(waveBg, wx, ty, ww, TrackHeight);

        // Shade the span the clip actually occupies, so a moved clip is obvious.
        if (i != _liveTrack && track.Samples.Length > 0)
        {
            double cx0 = Math.Max(HeaderWidth, SampleToX(track.Offset));
            double cx1 = Math.Min(ContentRight, SampleToX(track.Offset + track.Samples.Length));
            if (cx1 > cx0)
                using (var clipBg = new SolidBrush(Color.FromArgb(42, 46, 60)))
                    g.FillRectangle(clipBg, (float)cx0, ty, (float)(cx1 - cx0), TrackHeight);
        }

        int midY = ty + TrackHeight / 2;
        using (var midPen = new Pen(Color.FromArgb(58, 62, 74)))
            g.DrawLine(midPen, wx, midY, ContentRight, midY);

        int half = TrackHeight / 2 - 8;
        if (i == _liveTrack)
            DrawLiveLane(g, midY, half);
        else
            DrawWaveLane(g, i, midY, half);

        using (var sep = new Pen(Color.FromArgb(20, 21, 26)))
            g.DrawLine(sep, 0, ty + TrackHeight, Width, ty + TrackHeight);
    }

    private void DrawWaveLane(Graphics g, int i, int midY, int half)
    {
        var track = _project!.Tracks[i];
        int len = track.Samples.Length;
        if (len == 0) return;
        int off = track.Offset;
        using var pen = new Pen(Color.FromArgb(120, 200, 255));
        for (int x = HeaderWidth; x < ContentRight; x++)
        {
            int s0 = XToSample(x) - off;          // track-local sample under this column
            int s1 = XToSample(x + 1) - off;
            if (s1 <= s0) s1 = s0 + 1;
            if (s0 >= len) break;                  // past the end of the clip
            if (s1 <= 0) continue;                 // before the start of the clip
            if (s0 < 0) s0 = 0;
            if (s1 > len) s1 = len;

            ColumnPeak(i, s0, s1, out float mn, out float mx);
            int yTop = midY - (int)(mx * half);
            int yBot = midY - (int)(mn * half);
            if (yBot < yTop) (yTop, yBot) = (yBot, yTop);
            if (yBot == yTop) yBot = yTop + 1;
            g.DrawLine(pen, x, yTop, x, yBot);
        }
    }

    private void ColumnPeak(int i, int s0, int s1, out float mn, out float mx)
    {
        mn = 1f;
        mx = -1f;
        var pmin = _peakMin[i];
        var pmax = _peakMax[i];
        if (s1 - s0 >= PeakBucket && pmin.Length > 0)
        {
            int b0 = s0 / PeakBucket;
            int b1 = Math.Min(pmax.Length, (s1 + PeakBucket - 1) / PeakBucket);
            for (int b = b0; b < b1; b++)
            {
                if (pmin[b] < mn) mn = pmin[b];
                if (pmax[b] > mx) mx = pmax[b];
            }
        }
        else
        {
            var s = _project!.Tracks[i].Samples;
            for (int k = s0; k < s1 && k < s.Length; k++)
            {
                if (s[k] < mn) mn = s[k];
                if (s[k] > mx) mx = s[k];
            }
        }
        if (mx < mn) { mn = 0f; mx = 0f; }
    }

    private void DrawLiveLane(Graphics g, int midY, int half)
    {
        using var pen = new Pen(Color.FromArgb(255, 120, 120));
        int count = _liveMax.Count;
        int width = ContentRight - HeaderWidth;
        int first = Math.Max(0, count - width);
        for (int x = 0; first + x < count && x < width; x++)
        {
            float mn = _liveMin[first + x];
            float mx = _liveMax[first + x];
            int yTop = midY - (int)(mx * half);
            int yBot = midY - (int)(mn * half);
            if (yBot < yTop) (yTop, yBot) = (yBot, yTop);
            if (yBot == yTop) yBot = yTop + 1;
            g.DrawLine(pen, HeaderWidth + x, yTop, HeaderWidth + x, yBot);
        }
        using var brush = new SolidBrush(Color.FromArgb(255, 120, 120));
        g.DrawString("●  Recording…", _smallFont, brush, HeaderWidth + 8, midY - half - 14);
    }

    private void DrawRuler(Graphics g)
    {
        using var tickPen = new Pen(Color.FromArgb(110, 115, 128));
        using var textBrush = new SolidBrush(Color.FromArgb(155, 160, 172));

        double visibleSec = WaveWidth * _samplesPerPixel / Rate;
        double step = NiceStep(visibleSec / 8.0);
        double startSec = _offset / (double)Rate;
        double firstTick = Math.Ceiling(startSec / step) * step;

        for (double t = firstTick; ; t += step)
        {
            double x = SampleToX((int)(t * Rate));
            if (x > ContentRight) break;
            if (x >= HeaderWidth)
            {
                g.DrawLine(tickPen, (float)x, RulerHeight - 6, (float)x, RulerHeight);
                g.DrawString(FormatTime(t), _smallFont, textBrush, (float)x + 2, 2);
            }
        }
    }

    private void DrawOverlay(Graphics g)
    {
        int top = RulerHeight;
        int bottom = ContentBottom;

        if (HasSelection)
        {
            double x0 = Math.Max(HeaderWidth, SampleToX(_selStart));
            double x1 = Math.Min(ContentRight, SampleToX(_selEnd));
            if (x1 > x0)
            {
                using var sel = new SolidBrush(Color.FromArgb(70, 120, 170, 255));
                g.FillRectangle(sel, (float)x0, top, (float)(x1 - x0), bottom - top);
            }
        }

        double cx = SampleToX(_selStart);
        if (cx >= HeaderWidth && cx <= ContentRight)
        {
            using var cursorPen = new Pen(Color.FromArgb(240, 240, 240));
            g.DrawLine(cursorPen, (float)cx, top, (float)cx, bottom);
        }

        if (_playHead >= 0)
        {
            double px = SampleToX(_playHead);
            if (px >= HeaderWidth && px <= ContentRight)
            {
                using var playPen = new Pen(Color.FromArgb(120, 230, 130), 1.5f);
                g.DrawLine(playPen, (float)px, top, (float)px, bottom);
            }
        }
    }

    private void DrawHint(Graphics g)
    {
        using var brush = new SolidBrush(Color.FromArgb(130, 135, 145));
        var text = "Press  ● Record,  use  + Add Track,  drag an audio file here,  or  File ▸ Open.";
        var size = g.MeasureString(text, Font);
        g.DrawString(text, Font, brush,
            HeaderWidth + (WaveWidth - size.Width) / 2f,
            RulerHeight + (ContentBottom - RulerHeight - size.Height) / 2f);
    }

    private static double NiceStep(double target)
    {
        double[] steps =
        {
            0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5,
            1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800
        };
        foreach (double s in steps)
            if (s >= target) return s;
        return steps[^1];
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int minutes = (int)(seconds / 60);
        double rest = seconds - minutes * 60;
        return $"{minutes}:{rest:00.000}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_project != null) _project.Changed -= OnProjectChanged;
            _smallFont.Dispose();
            _nameFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
