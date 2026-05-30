using System.Runtime.InteropServices;

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

    private readonly List<(int track, float[] samples, int offset)> _undo = new();
    private readonly List<(int track, float[] samples, int offset)> _redo = new();
    private const int MaxUndo = 30;

    /// <summary>Raised after any change to track content, structure, or mute state.</summary>
    public event EventHandler? Changed;

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsEmpty => _tracks.Count == 0 || _tracks.All(t => t.Length == 0);

    /// <summary>End of the latest-finishing track on the timeline, in samples.</summary>
    public int Length
    {
        get
        {
            int max = 0;
            foreach (var t in _tracks)
                if (t.End > max) max = t.End;
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

    // --- timeline position (move a track left/right) -----------------------

    public int GetOffset(int track) => Valid(track) ? _tracks[track].Offset : 0;

    /// <summary>Live move during a drag — no undo snapshot (call CommitMove on release).</summary>
    public void SetOffset(int track, int offset)
    {
        if (!Valid(track)) return;
        offset = Math.Max(0, offset);
        if (_tracks[track].Offset == offset) return;
        _tracks[track].Offset = offset;
        Raise();
    }

    /// <summary>Records one undo step for a finished move (restores the pre-drag offset).</summary>
    public void CommitMove(int track, int oldOffset)
    {
        if (!Valid(track) || _tracks[track].Offset == oldOffset) return;
        _undo.Add((track, _tracks[track].Samples, oldOffset));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    // --- project file format ----------------------------------------------
    // Binary .smsproj layout:
    //   4 bytes "SBMS"
    //   int32 format version (1 or 2)
    //   int32 sample rate
    //   int32 track count
    //   per track:
    //     int16 name byte length, name bytes (UTF-8)
    //     byte  muted (0/1)
    //     int32 timeline offset in samples         <-- v2 only
    //     int32 sample count, sample count * float32 samples

    private const uint Magic = 0x534D4253; // "SBMS"
    private const int CurrentFormat = 2;

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(Magic);
            w.Write(CurrentFormat);
            w.Write(Rate);
            w.Write(_tracks.Count);
            foreach (var t in _tracks)
            {
                var nameBytes = System.Text.Encoding.UTF8.GetBytes(t.Name ?? "");
                if (nameBytes.Length > short.MaxValue)
                    nameBytes = nameBytes[..short.MaxValue];
                w.Write((short)nameBytes.Length);
                w.Write(nameBytes);
                w.Write((byte)(t.Muted ? 1 : 0));
                w.Write(t.Offset);
                w.Write(t.Samples.Length);
                var bytes = MemoryMarshal.AsBytes(t.Samples.AsSpan());
                w.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    public void LoadFromBytes(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a SlinnerB's Music Studio project file.");
        int format = r.ReadInt32();
        if (format != 1 && format != 2)
            throw new InvalidDataException($"Unsupported project format version {format}.");
        int rate = r.ReadInt32();
        if (rate != Rate)
            throw new InvalidDataException($"Sample rate mismatch: file is {rate}, app expects {Rate}.");
        int trackCount = r.ReadInt32();

        var fresh = new List<AudioTrack>();
        for (int i = 0; i < trackCount; i++)
        {
            int nameLen = r.ReadInt16();
            string name = System.Text.Encoding.UTF8.GetString(r.ReadBytes(nameLen));
            bool muted = r.ReadByte() != 0;
            int offset = format >= 2 ? r.ReadInt32() : 0;
            int sampleCount = r.ReadInt32();
            var samples = new float[sampleCount];
            var bytes = MemoryMarshal.AsBytes(samples.AsSpan());
            int read = r.Read(bytes);
            if (read != bytes.Length)
                throw new InvalidDataException("Project file is truncated.");
            fresh.Add(new AudioTrack { Name = name, Muted = muted, Offset = Math.Max(0, offset), Samples = samples });
        }

        _tracks.Clear();
        _tracks.AddRange(fresh);
        _undo.Clear();
        _redo.Clear();
        Raise();
    }

    // --- undo --------------------------------------------------------------

    private void Snapshot(int track)
    {
        _undo.Add((track, _tracks[track].Samples, _tracks[track].Offset));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (track, samples, offset) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        if (Valid(track))
        {
            _redo.Add((track, _tracks[track].Samples, _tracks[track].Offset));
            _tracks[track].Samples = samples;
            _tracks[track].Offset = offset;
        }
        Raise();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var (track, samples, offset) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        if (Valid(track))
        {
            _undo.Add((track, _tracks[track].Samples, _tracks[track].Offset));
            _tracks[track].Samples = samples;
            _tracks[track].Offset = offset;
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
        _tracks[track].Offset += start;   // keep the kept region where it sits on the timeline
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

    // RMS-based loudness normalize. Boosts perceived loudness to hit a target
    // RMS level in dBFS, then hard-limits at -0.1 dBFS to prevent clipping.
    // Quiet recordings get much louder than peak Normalize would deliver.
    public void LoudnessNormalize(int track, int start, int end, float targetRmsDb)
    {
        if (!Valid(track)) return;
        (start, end) = ClampRange(track, start, end);
        if (end <= start) return;
        var samples = _tracks[track].Samples;

        double sumSq = 0;
        int count = end - start;
        for (int i = start; i < end; i++)
            sumSq += (double)samples[i] * samples[i];
        double rms = Math.Sqrt(sumSq / count);
        if (rms < 1e-6) return;

        double currentDb = 20.0 * Math.Log10(rms);
        double gainDb = targetRmsDb - currentDb;
        float gain = (float)Math.Pow(10.0, gainDb / 20.0);

        Amplify(track, start, end, gain);   // Amplify clamps to [-1,1] and takes the undo snapshot
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

    /// <summary>Sums every non-muted track into one buffer at its timeline offset, clipped to [-1, 1].</summary>
    public float[] Mixdown()
    {
        int len = Length;
        var mix = new float[len];
        foreach (var track in _tracks)
        {
            if (track.Muted) continue;
            var s = track.Samples;
            int off = track.Offset;
            for (int i = 0; i < s.Length; i++)
            {
                int j = off + i;
                if (j >= 0 && j < len) mix[j] += s[i];
            }
        }
        for (int i = 0; i < len; i++)
            mix[i] = Math.Clamp(mix[i], -1f, 1f);
        return mix;
    }
}
