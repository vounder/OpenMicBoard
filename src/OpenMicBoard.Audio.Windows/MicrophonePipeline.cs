using NAudio.Wave;

namespace OpenMicBoard.Audio.Windows;

internal sealed class BufferedMicrophoneSampleProvider : ISampleProvider
{
    private const float SampleRate = 48_000f;
    private const float CompressorThresholdDb = -18f;
    private const float CompressorRatio = 3f;
    private const float MakeupGainDb = 6f;
    private static readonly float HighPassCoefficient =
        1f / (1f + ((2f * MathF.PI * 80f) / SampleRate));
    private static readonly float LevelAttack = TimeCoefficient(0.005f);
    private static readonly float LevelRelease = TimeCoefficient(0.08f);
    private static readonly float GateAttack = TimeCoefficient(0.003f);
    private static readonly float GateRelease = TimeCoefficient(0.18f);
    private static readonly float CompressorAttack = TimeCoefficient(0.01f);
    private static readonly float CompressorRelease = TimeCoefficient(0.12f);
    private readonly AdaptiveStereoBuffer _buffer;
    private float _gain;
    private float _gateThresholdDb;
    private int _voiceProcessingEnabled;
    private int _noiseGateEnabled;
    private int _muted;
    private float _previousInputLeft;
    private float _previousInputRight;
    private float _previousOutputLeft;
    private float _previousOutputRight;
    private float _levelEnvelope;
    private float _gateGain;
    private float _compressorGain = 1f;
    private bool _gateOpen;

    public BufferedMicrophoneSampleProvider(
        AdaptiveStereoBuffer buffer,
        float gain,
        bool voiceProcessingEnabled = true,
        bool noiseGateEnabled = true,
        float noiseGateThresholdDb = -50f,
        bool muted = false)
    {
        _buffer = buffer;
        Configure(
            gain,
            voiceProcessingEnabled,
            noiseGateEnabled,
            noiseGateThresholdDb,
            muted);
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    public void Configure(
        float gain,
        bool voiceProcessingEnabled,
        bool noiseGateEnabled,
        float noiseGateThresholdDb,
        bool muted)
    {
        Volatile.Write(ref _gain, Math.Clamp(gain, 0f, 4f));
        Volatile.Write(ref _voiceProcessingEnabled, voiceProcessingEnabled ? 1 : 0);
        Volatile.Write(ref _noiseGateEnabled, noiseGateEnabled ? 1 : 0);
        Volatile.Write(ref _gateThresholdDb, Math.Clamp(noiseGateThresholdDb, -70f, -25f));
        Volatile.Write(ref _muted, muted ? 1 : 0);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var span = buffer.AsSpan(offset, count);
        _buffer.Read(span);
        if (Volatile.Read(ref _muted) != 0)
        {
            span.Clear();
            _levelEnvelope = 0f;
            _gateGain = 0f;
            _gateOpen = false;
            return count;
        }

        var inputGain = Volatile.Read(ref _gain);
        var voiceProcessingEnabled = Volatile.Read(ref _voiceProcessingEnabled) != 0;
        var noiseGateEnabled = Volatile.Read(ref _noiseGateEnabled) != 0;
        var gateThreshold = DbToLinear(Volatile.Read(ref _gateThresholdDb));
        var gateCloseThreshold = gateThreshold * 0.5f;
        var makeupGain = DbToLinear(MakeupGainDb);

        for (var index = 0; index < span.Length; index += 2)
        {
            var left = span[index] * inputGain;
            var right = span[index + 1] * inputGain;

            if (voiceProcessingEnabled || noiseGateEnabled)
            {
                var filteredLeft =
                    HighPassCoefficient * (_previousOutputLeft + left - _previousInputLeft);
                var filteredRight =
                    HighPassCoefficient * (_previousOutputRight + right - _previousInputRight);
                _previousInputLeft = left;
                _previousInputRight = right;
                _previousOutputLeft = filteredLeft;
                _previousOutputRight = filteredRight;
                left = filteredLeft;
                right = filteredRight;
            }

            var level = Math.Max(Math.Abs(left), Math.Abs(right));
            var levelCoefficient = level > _levelEnvelope ? LevelAttack : LevelRelease;
            _levelEnvelope += (level - _levelEnvelope) * levelCoefficient;

            if (noiseGateEnabled)
            {
                if (!_gateOpen && _levelEnvelope >= gateThreshold)
                {
                    _gateOpen = true;
                }
                else if (_gateOpen && _levelEnvelope < gateCloseThreshold)
                {
                    _gateOpen = false;
                }

                var targetGateGain = _gateOpen ? 1f : 0f;
                var gateCoefficient = targetGateGain > _gateGain ? GateAttack : GateRelease;
                _gateGain += (targetGateGain - _gateGain) * gateCoefficient;
            }
            else
            {
                _gateOpen = true;
                _gateGain = 1f;
            }

            if (voiceProcessingEnabled)
            {
                var desiredCompressorGain = makeupGain;
                if (_levelEnvelope > DbToLinear(CompressorThresholdDb))
                {
                    var levelDb = LinearToDb(_levelEnvelope);
                    var compressedDb =
                        CompressorThresholdDb +
                        ((levelDb - CompressorThresholdDb) / CompressorRatio);
                    desiredCompressorGain *= DbToLinear(compressedDb - levelDb);
                }

                var compressorCoefficient =
                    desiredCompressorGain < _compressorGain
                        ? CompressorAttack
                        : CompressorRelease;
                _compressorGain +=
                    (desiredCompressorGain - _compressorGain) * compressorCoefficient;
            }
            else
            {
                _compressorGain = 1f;
            }

            var outputGain = _gateGain * _compressorGain;
            span[index] = Math.Clamp(left * outputGain, -4f, 4f);
            span[index + 1] = Math.Clamp(right * outputGain, -4f, 4f);
        }

        return count;
    }

    private static float TimeCoefficient(float seconds) =>
        1f - MathF.Exp(-1f / (SampleRate * seconds));

    private static float DbToLinear(float decibels) =>
        MathF.Pow(10f, decibels / 20f);

    private static float LinearToDb(float linear) =>
        20f * MathF.Log10(Math.Max(linear, 0.000_001f));
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
