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

    public int Length => Samples.Length;
}
