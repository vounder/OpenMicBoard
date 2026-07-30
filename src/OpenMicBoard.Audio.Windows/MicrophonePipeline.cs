using NAudio.Wave;

namespace OpenMicBoard.Audio.Windows;

internal sealed class BufferedMicrophoneSampleProvider : ISampleProvider
{
    private readonly AdaptiveStereoBuffer _buffer;

    public BufferedMicrophoneSampleProvider(AdaptiveStereoBuffer buffer, float gain)
    {
        _buffer = buffer;
        Gain = gain;
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    public float Gain { get; set; }

    public int Read(float[] buffer, int offset, int count)
    {
        var span = buffer.AsSpan(offset, count);
        _buffer.Read(span);
        if (Math.Abs(Gain - 1f) > float.Epsilon)
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] *= Gain;
            }
        }

        return count;
    }
}

internal sealed class MicrophonePcmConverter
{
    private readonly WaveFormat _sourceFormat;
    private readonly AdaptiveStereoBuffer _target;
    private readonly bool _isFloat;
    private float[] _decodedStereo = new float[8_192];
    private float[] _resampledStereo = new float[8_192];
    private double _sourcePosition;
    private bool _hasPreviousFrame;
    private float _previousLeft;
    private float _previousRight;

    public MicrophonePcmConverter(WaveFormat sourceFormat, AdaptiveStereoBuffer target)
    {
        _sourceFormat = sourceFormat;
        _target = target;
        _isFloat = sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                   (sourceFormat is WaveFormatExtensible extensible &&
                    extensible.SubFormat ==
                    new Guid("00000003-0000-0010-8000-00aa00389b71"));

        if (sourceFormat.Channels is < 1 or > 8)
        {
            throw new NotSupportedException($"Unsupported microphone channel count: {sourceFormat.Channels}.");
        }

        if (sourceFormat.Encoding is not (
            WaveFormatEncoding.IeeeFloat or
            WaveFormatEncoding.Pcm or
            WaveFormatEncoding.Extensible))
        {
            throw new NotSupportedException($"Unsupported microphone format: {sourceFormat}.");
        }
    }

    public void ConvertAndWrite(byte[] source, int byteCount)
    {
        var bytesPerSample = _sourceFormat.BitsPerSample / 8;
        if (bytesPerSample is not (2 or 3 or 4))
        {
            throw new NotSupportedException(
                $"Unsupported microphone bit depth: {_sourceFormat.BitsPerSample}.");
        }

        var frameBytes = bytesPerSample * _sourceFormat.Channels;
        var sourceFrames = byteCount / frameBytes;
        if (sourceFrames == 0)
        {
            return;
        }

        EnsureCapacity(sourceFrames * 2);

        for (var frame = 0; frame < sourceFrames; frame++)
        {
            var left = DecodeSample(source, (frame * frameBytes), bytesPerSample);
            var right = _sourceFormat.Channels == 1
                ? left
                : DecodeSample(source, (frame * frameBytes) + bytesPerSample, bytesPerSample);
            _decodedStereo[frame * 2] = left;
            _decodedStereo[(frame * 2) + 1] = right;
        }

        var ratio = _sourceFormat.SampleRate / 48_000d;
        var outputFrames = 0;
        var position = _sourcePosition;
        while (true)
        {
            var firstFrame = (int)Math.Floor(position);
            var fraction = (float)(position - firstFrame);
            float firstLeft;
            float firstRight;
            float nextLeft;
            float nextRight;

            if (firstFrame == -1 && _hasPreviousFrame)
            {
                firstLeft = _previousLeft;
                firstRight = _previousRight;
                nextLeft = _decodedStereo[0];
                nextRight = _decodedStereo[1];
            }
            else if (firstFrame >= 0 && firstFrame + 1 < sourceFrames)
            {
                var first = firstFrame * 2;
                var next = first + 2;
                firstLeft = _decodedStereo[first];
                firstRight = _decodedStereo[first + 1];
                nextLeft = _decodedStereo[next];
                nextRight = _decodedStereo[next + 1];
            }
            else if (firstFrame == sourceFrames - 1 && fraction < 1e-9f)
            {
                var last = firstFrame * 2;
                firstLeft = _decodedStereo[last];
                firstRight = _decodedStereo[last + 1];
                nextLeft = firstLeft;
                nextRight = firstRight;
            }
            else
            {
                break;
            }

            EnsureCapacity((outputFrames + 1) * 2);
            _resampledStereo[outputFrames * 2] =
                firstLeft + ((nextLeft - firstLeft) * fraction);
            _resampledStereo[(outputFrames * 2) + 1] =
                firstRight + ((nextRight - firstRight) * fraction);

            outputFrames++;
            position += ratio;
        }

        var lastFrame = (sourceFrames - 1) * 2;
        _previousLeft = _decodedStereo[lastFrame];
        _previousRight = _decodedStereo[lastFrame + 1];
        _hasPreviousFrame = true;
        _sourcePosition = position - sourceFrames;
        _target.Write(_resampledStereo.AsSpan(0, outputFrames * 2));
    }

    private float DecodeSample(byte[] source, int offset, int bytesPerSample)
    {
        if (_isFloat && bytesPerSample == 4)
        {
            return BitConverter.ToSingle(source, offset);
        }

        return bytesPerSample switch
        {
            2 => BitConverter.ToInt16(source, offset) / 32_768f,
            3 => Decode24Bit(source, offset) / 8_388_608f,
            4 => BitConverter.ToInt32(source, offset) / 2_147_483_648f,
            _ => 0
        };
    }

    private static int Decode24Bit(byte[] source, int offset)
    {
        var value = source[offset] | (source[offset + 1] << 8) | (source[offset + 2] << 16);
        return (value & 0x0080_0000) != 0 ? value | unchecked((int)0xFF00_0000) : value;
    }

    private void EnsureCapacity(int sampleCount)
    {
        if (_decodedStereo.Length < sampleCount)
        {
            Array.Resize(ref _decodedStereo, NextPowerOfTwo(sampleCount));
        }

        if (_resampledStereo.Length < sampleCount)
        {
            Array.Resize(ref _resampledStereo, NextPowerOfTwo(sampleCount));
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }
}
