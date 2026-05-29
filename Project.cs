namespace SlinnerBMusicStudio;

/// <summary>
/// A multi-track project. Each track is an independent mono buffer; editing
/// operations target one track by index and never mutate buffers in place.
/// Undo is global: every edit records the affected track plus its previous
/// buffer, so a snapshot is just an array reference (no copy).
/// </summary>
public class Project
{
    public const int Rate = 44100;

    private readonly List<AudioTrack> _tracks = new();
    public IReadOnlyList<AudioTrack> Tracks => _tracks;
    public int TrackCount => _tracks.Count;

    private readonly List<(int track, float[] samples)> _undo = new();
    private readonly List<(int track, float[] samples)> _redo = new();
    private const int MaxUndo = 30;

    /// <summary>Raised after any change to track content, structure, or mute state.</summary>
    public event EventHandler? Changed;

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsEmpty => _tracks.Count == 0 || _tracks.All(t => t.Length == 0);

    /// <summary>Length of the longest track, in samples.</summary>
    public int Length
    {
        get
        {
            int max = 0;
            foreach (var t in _tracks)
                if (t.Length > max) max = t.Length;
            return max;
        }
    }

    public double Duration => Length / (double)Rate;

    private bool Valid(int track) => track >= 0 && track < _tracks.Count;

    // --- structure ---------------------------------------------------------

    public AudioTrack AddTrack(string name, float[]? samples = null)
    {
        var track = new AudioTrack { Name = name, Samples = samples ?? Array.Empty<float>() };
        _tracks.Add(track);
        _undo.Clear();
        _redo.Clear();
        Raise();
        return track;
    }

    public void RemoveTrack(int index)
    {
        if (!Valid(index)) return;
        _tracks.RemoveAt(index);
        _undo.Clear();
        _redo.Clear();
        Raise();
    }

    public void Clear()
    {
        _tracks.Clear();
        _undo.Clear();
        _redo.Clear();
        Raise();
    }

    /// <summary>Replaces the whole project with a single track (used by File ▸ Open).</summary>
    public void LoadSingle(float[] samples, string name)
    {
        _tracks.Clear();
        _tracks.Add(new AudioTrack { Name = name, Samples = samples });
        _undo.Clear();
        _redo.Clear();
        Raise();
    }

    /// <summary>Stores a finished recording into a track. Not an undoable edit.</summary>
    public void SetTrackSamples(int index, float[] samples)
    {
        if (!Valid(index)) return;
        _tracks[index].Samples = samples;
        _undo.Clear();
        _redo.Clear();
        Raise();
    }

    public void SetMuted(int index, bool muted)
    {
        if (!Valid(index)) return;
        _tracks[index].Muted = muted;
        Raise();
    }

    // --- undo --------------------------------------------------------------

    private void Snapshot(int track)
    {
        _undo.Add((track, _tracks[track].Samples));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (track, samples) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        if (Valid(track))
        {
            _redo.Add((track, _tracks[track].Samples));
            _tracks[track].Samples = samples;
        }
        Raise();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var (track, samples) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        if (Valid(track))
        {
            _undo.Add((track, _tracks[track].Samples));
            _tracks[track].Samples = samples;
        }
        Raise();
    }

    // --- editing (one track at a time) -------------------------------------

    private (int start, int end) ClampRange(int track, int start, int end)
    {
        int len = _tracks[track].Length;
        start = Math.Clamp(start, 0, len);
        end = Math.Clamp(end, 0, len);
        if (end < start) (start, end) = (end, start);
        return (start, end);
    }

    /// <summary>Returns a copy of a track's [start, end) without modifying it.</summary>
    public float[] Extract(int track, int start, int end)
    {
        if (!Valid(track)) return Array.Empty<float>();
        (start, end) = ClampRange(track, start, end);
        var result = new float[end - start];
        Array.Copy(_tracks[track].Samples, start, result, 0, end - start);
        return result;
    }

    public void Delete(int track, int start, int end)
    {
        if (!Valid(track)) return;
        (start, end) = ClampRange(track, start, end);
        if (end <= start) return;
        Snapshot(track);
        var src = _tracks[track].Samples;
        var result = new float[src.Length - (end - start)];
        Array.Copy(src, 0, result, 0, start);
        Array.Copy(src, end, result, start, src.Length - end);
        _tracks[track].Samples = result;
        Raise();
    }

    public void Insert(int track, int pos, float[] data)
    {
        if (!Valid(track) || data.Length == 0) return;
        var src = _tracks[track].Samples;
        pos = Math.Clamp(pos, 0, src.Length);
        Snapshot(track);
        var result = new float[src.Length + data.Length];
        Array.Copy(src, 0, result, 0, pos);
        Array.Copy(data, 0, result, pos, data.Length);
        Array.Copy(src, pos, result, pos + data.Length, src.Length - pos);
        _tracks[track].Samples = result;
        Raise();
    }

    public void Trim(int track, int start, int end)
    {
        if (!Valid(track)) return;
        (start, end) = ClampRange(track, start, end);
        var src = _tracks[track].Samples;
        if (start == 0 && end == src.Length) return;
        Snapshot(track);
        var result = new float[end - start];
        Array.Copy(src, start, result, 0, end - start);
        _tracks[track].Samples = result;
        Raise();
    }

    public void Silence(int track, int start, int end)
    {
        if (!Valid(track)) return;
        (start, end) = ClampRange(track, start, end);
        if (end <= start) return;
        Snapshot(track);
        var result = (float[])_tracks[track].Samples.Clone();
        Array.Clear(result, start, end - start);
        _tracks[track].Samples = result;
        Raise();
    }

    public void Amplify(int track, int start, int end, float factor)
    {
        if (!Valid(track)) return;
        (start, end) = ClampRange(track, start, end);
        if (end <= start) return;
        Snapshot(track);
        var result = (float[])_tracks[track].Samples.Clone();
        for (int i = start; i < end; i++)
            result[i] = Math.Clamp(result[i] * factor, -1f, 1f);
        _tracks[track].Samples = result;
        Raise();
    }

    public void Normalize(int track, int start, int end)
    {
        if (!Valid(track)) return;
        (start, end) = ClampRange(track, start, end);
        if (end <= start) return;
        var samples = _tracks[track].Samples;
        float peak = 0f;
        for (int i = start; i < end; i++)
        {
            float a = Math.Abs(samples[i]);
            if (a > peak) peak = a;
        }
        if (peak < 1e-6f) return;
        Amplify(track, start, end, 0.99f / peak);   // Amplify takes the undo snapshot
    }

    public void FadeIn(int track, int start, int end)
    {
        if (!Valid(track)) return;
        (start, end) = ClampRange(track, start, end);
        if (end <= start) return;
        Snapshot(track);
        var result = (float[])_tracks[track].Samples.Clone();
        int n = end - start;
        for (int i = 0; i < n; i++)
            result[start + i] *= i / (float)n;
        _tracks[track].Samples = result;
        Raise();
    }

    public void FadeOut(int track, int start, int end)
    {
        if (!Valid(track)) return;
        (start, end) = ClampRange(track, start, end);
        if (end <= start) return;
        Snapshot(track);
        var result = (float[])_tracks[track].Samples.Clone();
        int n = end - start;
        for (int i = 0; i < n; i++)
            result[start + i] *= 1f - i / (float)n;
        _tracks[track].Samples = result;
        Raise();
    }

    // --- mixdown -----------------------------------------------------------

    /// <summary>Sums every non-muted track into one buffer, clipped to [-1, 1].</summary>
    public float[] Mixdown()
    {
        int len = Length;
        var mix = new float[len];
        foreach (var track in _tracks)
        {
            if (track.Muted) continue;
            var s = track.Samples;
            int n = Math.Min(s.Length, len);
            for (int i = 0; i < n; i++)
                mix[i] += s[i];
        }
        for (int i = 0; i < len; i++)
            mix[i] = Math.Clamp(mix[i], -1f, 1f);
        return mix;
    }
}
