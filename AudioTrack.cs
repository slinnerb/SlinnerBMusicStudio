namespace SlinnerBMusicStudio;

/// <summary>
/// One mono audio lane in the project (44.1 kHz, 32-bit float). This is a plain
/// data holder — all editing logic and undo handling live in <see cref="Project"/>.
/// </summary>
public class AudioTrack
{
    public float[] Samples = Array.Empty<float>();
    public string Name = "Track";
    public bool Muted;

    /// <summary>Where this track's audio starts on the timeline, in samples (>= 0).</summary>
    public int Offset;

    public int Length => Samples.Length;

    /// <summary>One past the last sample on the timeline (offset + length).</summary>
    public int End => Offset + Samples.Length;
}
