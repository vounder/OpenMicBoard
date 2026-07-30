using NAudio.Wave;

namespace OpenMicBoard.Audio.Windows;

internal sealed class LookaheadLimiterSampleProvider : ISampleProvider
{
    private const float Ceiling = 0.8912509f; // -1 dBFS
    private readonly ISampleProvider _source;
    private readonly float[] _sourceScratch = new float[16_384];
    private readonly float[] _delay;
    private int _delayIndex;
    private float _gain = 1f;

    public LookaheadLimiterSampleProvider(ISampleProvider source, int lookaheadMilliseconds = 5)
    {
        if (source.WaveFormat.Channels != 2)
        {
            throw new ArgumentException("The master limiter requires stereo input.", nameof(source));
        }

        _source = source;
        var delayFrames = Math.Max(1, source.WaveFormat.SampleRate * lookaheadMilliseconds / 1_000);
        _delay = new float[delayFrames * 2];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var processed = 0;
        while (processed < count)
        {
            var requested = Math.Min(_sourceScratch.Length, count - processed);
            requested -= requested & 1;
            if (requested == 0)
            {
                break;
            }

            var read = _source.Read(_sourceScratch, 0, requested);
            for (var index = 0; index < read; index += 2)
            {
                var peak = Math.Max(
                    Math.Abs(_sourceScratch[index]),
                    Math.Abs(_sourceScratch[index + 1]));
                var targetGain = peak > Ceiling ? Ceiling / peak : 1f;
                _gain = targetGain < _gain
                    ? targetGain
                    : Math.Min(1f, _gain + 0.00005f);

                var delayedLeft = _delay[_delayIndex];
                var delayedRight = _delay[_delayIndex + 1];
                _delay[_delayIndex] = _sourceScratch[index];
                _delay[_delayIndex + 1] = _sourceScratch[index + 1];
                _delayIndex += 2;
                if (_delayIndex >= _delay.Length)
                {
                    _delayIndex = 0;
                }

                buffer[offset + processed + index] =
                    Math.Clamp(delayedLeft * _gain, -Ceiling, Ceiling);
                buffer[offset + processed + index + 1] =
                    Math.Clamp(delayedRight * _gain, -Ceiling, Ceiling);
            }

            processed += read;
            if (read < requested)
            {
                break;
            }
        }

        if (processed < count)
        {
            Array.Clear(buffer, offset + processed, count - processed);
        }

        return count;
    }
}
