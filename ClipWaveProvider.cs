using NAudio.Wave;

namespace SlinnerBMusicStudio;

/// <summary>
/// Feeds a slice [start, end) of a mono float buffer to NAudio for playback or
/// file export, converting on the fly to 16-bit PCM. <see cref="Position"/>
/// exposes the current read point so the UI can draw a moving play-head.
/// </summary>
public class ClipWaveProvider : IWaveProvider
{
    private readonly float[] _samples;
    private readonly int _end;
    private int _pos;

    public WaveFormat WaveFormat { get; }

    public ClipWaveProvider(float[] samples, int sampleRate, int start, int end)
    {
        _samples = samples;
        _pos = Math.Clamp(start, 0, samples.Length);
        _end = Math.Clamp(end, _pos, samples.Length);
        WaveFormat = new WaveFormat(sampleRate, 16, 1);
    }

    /// <summary>Index of the next sample to be played.</summary>
    public int Position => _pos;

    public int Read(byte[] buffer, int offset, int count)
    {
        int frames = Math.Min(count / 2, _end - _pos);
        for (int i = 0; i < frames; i++)
        {
            short s = (short)(Math.Clamp(_samples[_pos + i], -1f, 1f) * 32767f);
            buffer[offset + i * 2] = (byte)(s & 0xFF);
            buffer[offset + i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        _pos += frames;
        return frames * 2;
    }
}
