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
    private readonly object _voiceMutationLock = new();
    private readonly object _playbackStateLock = new();
    private readonly HashSet<Guid> _playingPlaybackIds = [];
    private float _soundGain = 1f;
    private long _sequence;
    private bool _disposed;

    public FixedVoiceMixer(BufferedMicrophoneSampleProvider? microphone)
    {
        _microphone = microphone;
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    // This is deliberately internal plumbing. Its sole subscriber queues work before
    // notifying application code, so the audio callback never runs UI or user handlers.
    public event Action<AudioPlaybackStateChanged>? PlaybackStateChanged;

    public void SetSoundGain(float gain) =>
        Volatile.Write(ref _soundGain, Math.Clamp(gain, 0f, 4f));

    public void AddOrReplace(Guid playbackId, IAudioFileSource source, float volume)
    {
        lock (_voiceMutationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var provider = EnsureStereo48K(source);
            var volumeProvider = new VolumeSampleProvider(provider)
            {
                Volume = Math.Clamp(volume, 0f, 2f)
            };
            var replacement = new VoiceSlot(
                playbackId,
                volumeProvider,
                source,
                Interlocked.Increment(ref _sequence),
                Complete);

            for (var index = 0; index < VoiceCapacity; index++)
            {
                var existing = Volatile.Read(ref _voices[index]);
                if (existing?.PlaybackId == playbackId)
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
    }

    public void Stop(Guid playbackId)
    {
        foreach (var voice in _voices)
        {
            if (voice?.PlaybackId == playbackId)
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
            MixProvider(_microphone, _microphoneScratch, buffer, offset, count, gain: 1f);
        }

        var soundGain = Volatile.Read(ref _soundGain);
        for (var index = 0; index < VoiceCapacity; index++)
        {
            var voice = Volatile.Read(ref _voices[index]);
            if (voice is not null && !voice.IsCompleted)
            {
                MixProvider(voice, _voiceScratch[index], buffer, offset, count, soundGain);
            }
        }

        return count;
    }

    public void Dispose()
    {
        lock (_voiceMutationLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var disposedVoices = new List<VoiceSlot>(VoiceCapacity);
            lock (_playbackStateLock)
            {
                for (var index = 0; index < VoiceCapacity; index++)
                {
                    var voice = Interlocked.Exchange(ref _voices[index], null);
                    if (voice is null)
                    {
                        continue;
                    }

                    disposedVoices.Add(voice);
                    if (_playingPlaybackIds.Remove(voice.PlaybackId))
                    {
                        PlaybackStateChanged?.Invoke(new AudioPlaybackStateChanged(voice.PlaybackId, false));
                    }
                }
            }

            foreach (var voice in disposedVoices)
            {
                voice.Dispose();
            }
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
        int count,
        float gain)
    {
        var processed = 0;
        while (processed < count)
        {
            var requested = Math.Min(scratch.Length, count - processed);
            var read = source.Read(scratch, 0, requested);
            for (var index = 0; index < read; index++)
            {
                destination[destinationOffset + processed + index] += scratch[index] * gain;
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
        VoiceSlot? previous;
        lock (_playbackStateLock)
        {
            previous = Interlocked.Exchange(ref _voices[index], replacement);
            if (previous is not null &&
                previous.PlaybackId != replacement.PlaybackId &&
                _playingPlaybackIds.Remove(previous.PlaybackId))
            {
                PlaybackStateChanged?.Invoke(new AudioPlaybackStateChanged(previous.PlaybackId, false));
            }

            if (_playingPlaybackIds.Add(replacement.PlaybackId))
            {
                PlaybackStateChanged?.Invoke(new AudioPlaybackStateChanged(replacement.PlaybackId, true));
            }
        }

        previous?.Dispose();
    }

    private void Complete(VoiceSlot voice)
    {
        lock (_playbackStateLock)
        {
            for (var index = 0; index < VoiceCapacity; index++)
            {
                if (ReferenceEquals(Volatile.Read(ref _voices[index]), voice))
                {
                    if (_playingPlaybackIds.Remove(voice.PlaybackId))
                    {
                        PlaybackStateChanged?.Invoke(new AudioPlaybackStateChanged(voice.PlaybackId, false));
                    }

                    return;
                }
            }
        }
    }

    private sealed class VoiceSlot : ISampleProvider, IDisposable
    {
        private const int FadeSamples = 960;
        private readonly ISampleProvider _source;
        private readonly IDisposable _ownedSource;
        private readonly Action<VoiceSlot> _onCompleted;
        private int _fadeRemaining = -1;
        private int _completed;
        private int _activeReaders;
        private int _disposeRequested;
        private int _sourceDisposed;

        public VoiceSlot(
            Guid soundId,
            ISampleProvider source,
            IDisposable ownedSource,
            long sequence,
            Action<VoiceSlot> onCompleted)
        {
            PlaybackId = soundId;
            _source = source;
            _ownedSource = ownedSource;
            Sequence = sequence;
            _onCompleted = onCompleted;
        }

        public Guid PlaybackId { get; }

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
                    Complete();
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
                            Complete();
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
            Complete();
            Volatile.Write(ref _disposeRequested, 1);
            if (Volatile.Read(ref _activeReaders) == 0)
            {
                DisposeSource();
            }
        }

        private void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _onCompleted(this);
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
