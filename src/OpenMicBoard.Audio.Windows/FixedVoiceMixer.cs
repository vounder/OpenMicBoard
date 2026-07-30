using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OpenMicBoard.Audio.Windows;

internal sealed class FixedVoiceMixer : ISampleProvider, IDisposable
{
    public const int VoiceCapacity = 8;
    private const int ScratchSamples = 16_384;

    private readonly VoiceSlot?[] _voices = new VoiceSlot?[VoiceCapacity];
    private readonly float[][] _voiceScratch =
        Enumerable.Range(0, VoiceCapacity).Select(_ => new float[ScratchSamples]).ToArray();
    private readonly float[] _microphoneScratch = new float[ScratchSamples];
    private readonly BufferedMicrophoneSampleProvider? _microphone;
    private long _sequence;
    private bool _disposed;

    public FixedVoiceMixer(BufferedMicrophoneSampleProvider? microphone)
    {
        _microphone = microphone;
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    public void AddOrReplace(Guid soundId, IAudioFileSource source, float volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var provider = EnsureStereo48K(source);
        var volumeProvider = new VolumeSampleProvider(provider)
        {
            Volume = Math.Clamp(volume, 0f, 2f)
        };
        var replacement = new VoiceSlot(
            soundId,
            volumeProvider,
            source,
            Interlocked.Increment(ref _sequence));

        for (var index = 0; index < VoiceCapacity; index++)
        {
            var existing = Volatile.Read(ref _voices[index]);
            if (existing?.SoundId == soundId)
            {
                Swap(index, replacement);
                return;
            }
        }

        for (var index = 0; index < VoiceCapacity; index++)
        {
            var existing = Volatile.Read(ref _voices[index]);
            if (existing is null || existing.IsCompleted)
            {
                Swap(index, replacement);
                return;
            }
        }

        var oldestIndex = 0;
        var oldestSequence = long.MaxValue;
        for (var index = 0; index < VoiceCapacity; index++)
        {
            var sequence = Volatile.Read(ref _voices[index])?.Sequence ?? long.MinValue;
            if (sequence < oldestSequence)
            {
                oldestSequence = sequence;
                oldestIndex = index;
            }
        }

        Swap(oldestIndex, replacement);
    }

    public void Stop(Guid soundId)
    {
        foreach (var voice in _voices)
        {
            if (voice?.SoundId == soundId)
            {
                voice.RequestStop();
            }
        }
    }

    public void StopAll()
    {
        foreach (var voice in _voices)
        {
            voice?.RequestStop();
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        if (_microphone is not null)
        {
            MixProvider(_microphone, _microphoneScratch, buffer, offset, count);
        }

        for (var index = 0; index < VoiceCapacity; index++)
        {
            var voice = Volatile.Read(ref _voices[index]);
            if (voice is not null && !voice.IsCompleted)
            {
                MixProvider(voice, _voiceScratch[index], buffer, offset, count);
            }
        }

        return count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var index = 0; index < VoiceCapacity; index++)
        {
            Interlocked.Exchange(ref _voices[index], null)?.Dispose();
        }
    }

    private static ISampleProvider EnsureStereo48K(ISampleProvider source)
    {
        ISampleProvider provider = source;
        if (provider.WaveFormat.SampleRate != 48_000)
        {
            provider = new WdlResamplingSampleProvider(provider, 48_000);
        }

        provider = provider.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(provider),
            2 => provider,
            _ => throw new NotSupportedException(
                $"Audio files with {provider.WaveFormat.Channels} channels are not supported.")
        };

        return provider;
    }

    private static void MixProvider(
        ISampleProvider source,
        float[] scratch,
        float[] destination,
        int destinationOffset,
        int count)
    {
        var processed = 0;
        while (processed < count)
        {
            var requested = Math.Min(scratch.Length, count - processed);
            var read = source.Read(scratch, 0, requested);
            for (var index = 0; index < read; index++)
            {
                destination[destinationOffset + processed + index] += scratch[index];
            }

            processed += requested;
            if (read < requested)
            {
                break;
            }
        }
    }

    private void Swap(int index, VoiceSlot replacement)
    {
        var previous = Interlocked.Exchange(ref _voices[index], replacement);
        previous?.Dispose();
    }

    private sealed class VoiceSlot : ISampleProvider, IDisposable
    {
        private const int FadeSamples = 960;
        private readonly ISampleProvider _source;
        private readonly IDisposable _ownedSource;
        private int _fadeRemaining = -1;
        private int _completed;
        private int _activeReaders;
        private int _disposeRequested;
        private int _sourceDisposed;

        public VoiceSlot(
            Guid soundId,
            ISampleProvider source,
            IDisposable ownedSource,
            long sequence)
        {
            SoundId = soundId;
            _source = source;
            _ownedSource = ownedSource;
            Sequence = sequence;
        }

        public Guid SoundId { get; }

        public long Sequence { get; }

        public bool IsCompleted => Volatile.Read(ref _completed) != 0;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (IsCompleted)
            {
                return 0;
            }

            Interlocked.Increment(ref _activeReaders);
            try
            {
                if (IsCompleted)
                {
                    return 0;
                }

                var read = _source.Read(buffer, offset, count);
                if (read == 0)
                {
                    Volatile.Write(ref _completed, 1);
                    return 0;
                }

                var fadeRemaining = Volatile.Read(ref _fadeRemaining);
                if (fadeRemaining >= 0)
                {
                    for (var index = 0; index < read; index++)
                    {
                        var gain = Math.Clamp(
                            fadeRemaining / (float)FadeSamples,
                            0f,
                            1f);
                        buffer[offset + index] *= gain;
                        fadeRemaining--;
                        if (fadeRemaining <= 0)
                        {
                            Array.Clear(
                                buffer,
                                offset + index + 1,
                                read - index - 1);
                            Volatile.Write(ref _completed, 1);
                            break;
                        }
                    }

                    Volatile.Write(ref _fadeRemaining, fadeRemaining);
                }

                return read;
            }
            finally
            {
                if (Interlocked.Decrement(ref _activeReaders) == 0 &&
                    Volatile.Read(ref _disposeRequested) != 0)
                {
                    DisposeSource();
                }
            }
        }

        public void RequestStop() =>
            Interlocked.CompareExchange(ref _fadeRemaining, FadeSamples, -1);

        public void Dispose()
        {
            Volatile.Write(ref _completed, 1);
            Volatile.Write(ref _disposeRequested, 1);
            if (Volatile.Read(ref _activeReaders) == 0)
            {
                DisposeSource();
            }
        }

        private void DisposeSource()
        {
            if (Interlocked.Exchange(ref _sourceDisposed, 1) == 0)
            {
                _ownedSource.Dispose();
            }
        }
    }
}
