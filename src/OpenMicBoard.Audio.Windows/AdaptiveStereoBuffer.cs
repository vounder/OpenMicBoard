namespace OpenMicBoard.Audio.Windows;

/// <summary>
/// A bounded single-producer/single-consumer stereo buffer. The consumer
/// gently varies its read rate to compensate for independent capture and
/// render clocks without blocking the real-time render callback.
/// </summary>
public sealed class AdaptiveStereoBuffer
{
    private readonly float[] _samples;
    private readonly int _frameMask;
    private readonly int _targetFrames;
    private long _writeFrame;
    private long _readFrame;
    private double _phase;
    private bool _primed;

    public AdaptiveStereoBuffer(int capacityFrames = 262_144, int targetFrames = 4_800)
    {
        if (capacityFrames < 4 || (capacityFrames & (capacityFrames - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacityFrames),
                "Capacity must be a power of two.");
        }

        if (targetFrames < 2 || targetFrames >= capacityFrames / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFrames));
        }

        _samples = new float[capacityFrames * 2];
        _frameMask = capacityFrames - 1;
        _targetFrames = targetFrames;
    }

    public int AvailableFrames
    {
        get
        {
            var available = Volatile.Read(ref _writeFrame) - Volatile.Read(ref _readFrame);
            return (int)Math.Clamp(available, 0, _frameMask + 1L);
        }
    }

    public long DroppedFrames { get; private set; }

    public long UnderrunFrames { get; private set; }

    public int Write(ReadOnlySpan<float> interleavedStereo)
    {
        if ((interleavedStereo.Length & 1) != 0)
        {
            throw new ArgumentException("Stereo samples must be interleaved.", nameof(interleavedStereo));
        }

        var requestedFrames = interleavedStereo.Length / 2;
        var read = Volatile.Read(ref _readFrame);
        var write = _writeFrame;
        var freeFrames = _frameMask + 1L - (write - read);
        var framesToWrite = (int)Math.Min(requestedFrames, Math.Max(0, freeFrames));

        for (var frame = 0; frame < framesToWrite; frame++)
        {
            var destination = ((int)(write + frame) & _frameMask) * 2;
            _samples[destination] = interleavedStereo[frame * 2];
            _samples[destination + 1] = interleavedStereo[(frame * 2) + 1];
        }

        Volatile.Write(ref _writeFrame, write + framesToWrite);
        DroppedFrames += requestedFrames - framesToWrite;
        return framesToWrite;
    }

    public int Read(Span<float> destination)
    {
        if ((destination.Length & 1) != 0)
        {
            throw new ArgumentException("Stereo samples must be interleaved.", nameof(destination));
        }

        destination.Clear();
        var outputFrames = destination.Length / 2;
        var available = AvailableFrames;

        if (!_primed)
        {
            _primed = available >= Math.Max(2, _targetFrames / 4);
            if (!_primed)
            {
                UnderrunFrames += outputFrames;
                return outputFrames;
            }
        }

        if (available < 3)
        {
            _primed = false;
            UnderrunFrames += outputFrames;
            return outputFrames;
        }

        var fillError = (available - _targetFrames) / (double)_targetFrames;
        var correction = Math.Clamp(fillError * 0.0025, -0.005, 0.005);
        var ratio = 1.0 + correction;
        var readBase = Volatile.Read(ref _readFrame);
        var phase = _phase;
        var produced = 0;

        while (produced < outputFrames)
        {
            var wholeFrame = (long)phase;
            var nextFrame = wholeFrame + 1;
            if (nextFrame >= available)
            {
                break;
            }

            var fraction = (float)(phase - wholeFrame);
            var firstIndex = ((int)(readBase + wholeFrame) & _frameMask) * 2;
            var nextIndex = ((int)(readBase + nextFrame) & _frameMask) * 2;

            destination[produced * 2] =
                _samples[firstIndex] + ((_samples[nextIndex] - _samples[firstIndex]) * fraction);
            destination[(produced * 2) + 1] =
                _samples[firstIndex + 1] +
                ((_samples[nextIndex + 1] - _samples[firstIndex + 1]) * fraction);

            phase += ratio;
            produced++;
        }

        var consumed = (long)phase;
        _phase = phase - consumed;
        Volatile.Write(ref _readFrame, readBase + consumed);

        if (produced < outputFrames)
        {
            _primed = false;
            UnderrunFrames += outputFrames - produced;
        }

        return outputFrames;
    }

    public void Clear()
    {
        Volatile.Write(ref _readFrame, Volatile.Read(ref _writeFrame));
        _phase = 0;
        _primed = false;
    }
}
