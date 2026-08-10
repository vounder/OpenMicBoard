using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Concurrent;

namespace OpenMicBoard.Audio.Windows;

public sealed class WindowsAudioEngine : IAudioEngine
{
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly IAudioDeviceCatalog _catalog;
    private WasapiCapture? _capture;
    private WasapiOut? _render;
    private MMDevice? _captureDevice;
    private MMDevice? _renderDevice;
    private FixedVoiceMixer? _mixer;
    private BufferedMicrophoneSampleProvider? _microphoneProvider;
    private AdaptiveStereoBuffer? _microphoneBuffer;
    private MicrophonePcmConverter? _microphoneConverter;
    private AudioEngineOptions? _options;
    private readonly ConcurrentQueue<AudioPlaybackStateChanged> _playbackStateChanges = new();
    private int _playbackStateDispatchScheduled;
    private bool _stopping;
    private bool _disposed;

    public WindowsAudioEngine(IAudioDeviceCatalog? catalog = null)
    {
        _catalog = catalog ?? new AudioDeviceCatalog();
        _catalog.DevicesChanged += OnDevicesChanged;
    }

    public AudioEngineStatus Status { get; private set; } =
        new(AudioEngineState.Stopped);

    public event EventHandler<AudioEngineStatus>? StatusChanged;

    public event EventHandler<AudioPlaybackStateChanged>? PlaybackStateChanged;

    public async Task StartAsync(
        AudioEngineOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
            _options = options;
            StartInternal(options);
        }
        catch (Exception exception)
        {
            await StopInternalAsync().ConfigureAwait(false);
            SetStatus(AudioEngineState.Faulted, "Audio could not be started.", exception);
            throw;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _options = null;
            await StopInternalAsync().ConfigureAwait(false);
            SetStatus(AudioEngineState.Stopped);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task PlayAsync(
        Guid playbackId,
        string filePath,
        float volume = 1f,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Status.State != AudioEngineState.Running || _mixer is null)
        {
            throw new InvalidOperationException("Select an available virtual output first.");
        }

        var source = await Task.Run(() => AudioFileSource.Open(filePath), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            _mixer.AddOrReplace(
                playbackId,
                source,
                Math.Clamp(volume, 0f, 2f));
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public async Task PreviewAsync(
        string filePath,
        string previewEndpointId,
        float volume = 1f,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var previewDevice = GetActiveDevice(previewEndpointId, DataFlow.Render);
        using var source = await Task.Run(
            () => AudioFileSource.Open(filePath),
            cancellationToken);

        ISampleProvider provider = source;
        if (provider.WaveFormat.SampleRate != 48_000)
        {
            provider = new WdlResamplingSampleProvider(provider, 48_000);
        }

        provider = provider.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(provider),
            2 => provider,
            _ => throw new NotSupportedException("Preview supports mono and stereo files.")
        };
        provider = new VolumeSampleProvider(provider)
        {
            Volume = Math.Clamp(volume, 0f, 2f)
        };

        using var output = new WasapiOut(
            previewDevice,
            AudioClientShareMode.Shared,
            true,
            50);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(args.Exception);
            }
        };
        output.Init(provider);
        output.Play();

        using var registration = cancellationToken.Register(() =>
        {
            output.Stop();
            completion.TrySetCanceled(cancellationToken);
        });
        await completion.Task.ConfigureAwait(false);
    }

    public void StopSound(Guid playbackId) => _mixer?.Stop(playbackId);

    public void StopAllSounds() => _mixer?.StopAll();

    public void UpdateMixSettings(
        float microphoneGain,
        float soundGain,
        bool voiceProcessingEnabled,
        bool noiseGateEnabled,
        float noiseGateThresholdDb,
        bool microphoneMuted)
    {
        microphoneGain = Math.Clamp(microphoneGain, 0f, 4f);
        soundGain = Math.Clamp(soundGain, 0f, 4f);
        noiseGateThresholdDb = Math.Clamp(noiseGateThresholdDb, -70f, -25f);
        var options = _options;
        if (options is not null)
        {
            _options = options with
            {
                MicrophoneGain = microphoneGain,
                SoundGain = soundGain,
                VoiceProcessingEnabled = voiceProcessingEnabled,
                NoiseGateEnabled = noiseGateEnabled,
                NoiseGateThresholdDb = noiseGateThresholdDb,
                MicrophoneMuted = microphoneMuted
            };
        }

        _microphoneProvider?.Configure(
            microphoneGain,
            voiceProcessingEnabled,
            noiseGateEnabled,
            noiseGateThresholdDb,
            microphoneMuted);
        _mixer?.SetSoundGain(soundGain);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _catalog.DevicesChanged -= OnDevicesChanged;
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
            _stateGate.Dispose();
            _catalog.Dispose();
            _enumerator.Dispose();
        }
    }

    private void StartInternal(AudioEngineOptions options)
    {
        SetStatus(AudioEngineState.Starting);
        var bufferMilliseconds = options.BufferProfile switch
        {
            AudioBufferProfile.LowLatency => 20,
            AudioBufferProfile.Safe => 100,
            _ => 50
        };

        _renderDevice = GetActiveDevice(options.RenderEndpointId, DataFlow.Render);
        BufferedMicrophoneSampleProvider? microphoneProvider = null;
        if (!string.IsNullOrWhiteSpace(options.CaptureEndpointId))
        {
            _captureDevice = GetActiveDevice(options.CaptureEndpointId, DataFlow.Capture);
            _microphoneBuffer = new AdaptiveStereoBuffer(
                capacityFrames: 262_144,
                targetFrames: 4_800);
            microphoneProvider = new BufferedMicrophoneSampleProvider(
                _microphoneBuffer,
                Math.Clamp(options.MicrophoneGain, 0f, 4f),
                options.VoiceProcessingEnabled,
                options.NoiseGateEnabled,
                options.NoiseGateThresholdDb,
                options.MicrophoneMuted);
            _microphoneProvider = microphoneProvider;
            _microphoneConverter = new MicrophonePcmConverter(
                _captureDevice.AudioClient.MixFormat,
                _microphoneBuffer);
            _capture = new WasapiCapture(_captureDevice);
            _capture.DataAvailable += OnCaptureDataAvailable;
            _capture.RecordingStopped += OnCaptureStopped;
        }

        _mixer = new FixedVoiceMixer(microphoneProvider);
        _mixer.SetSoundGain(options.SoundGain);
        _mixer.PlaybackStateChanged += OnMixerPlaybackStateChanged;
        var limiter = new LookaheadLimiterSampleProvider(_mixer);
        _render = new WasapiOut(
            _renderDevice,
            AudioClientShareMode.Shared,
            true,
            bufferMilliseconds);
        _render.PlaybackStopped += OnRenderStopped;
        _render.Init(limiter);

        _capture?.StartRecording();
        _render.Play();
        SetStatus(AudioEngineState.Running);
    }

    private async Task StopInternalAsync()
    {
        _stopping = true;
        try
        {
            _mixer?.StopAll();
            _capture?.StopRecording();
            _render?.Stop();
            await Task.Yield();

            if (_capture is not null)
            {
                _capture.DataAvailable -= OnCaptureDataAvailable;
                _capture.RecordingStopped -= OnCaptureStopped;
                _capture.Dispose();
            }

            if (_render is not null)
            {
                _render.PlaybackStopped -= OnRenderStopped;
                _render.Dispose();
            }

            if (_mixer is not null)
            {
                _mixer.Dispose();
                _mixer.PlaybackStateChanged -= OnMixerPlaybackStateChanged;
            }
            _captureDevice?.Dispose();
            _renderDevice?.Dispose();
            _capture = null;
            _render = null;
            _captureDevice = null;
            _renderDevice = null;
            _mixer = null;
            _microphoneBuffer = null;
            _microphoneProvider = null;
            _microphoneConverter = null;
        }
        finally
        {
            _stopping = false;
        }
    }

    private MMDevice GetActiveDevice(string endpointId, DataFlow expectedFlow)
    {
        if (!IsActive(endpointId, expectedFlow))
        {
            throw new InvalidOperationException("The selected audio endpoint is unavailable.");
        }

        return _enumerator.GetDevice(endpointId);
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs args)
    {
        try
        {
            _microphoneConverter?.ConvertAndWrite(args.Buffer, args.BytesRecorded);
        }
        catch (Exception exception)
        {
            _ = TransitionToFaultedAsync("Microphone processing failed.", exception);
        }
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs args)
    {
        if (!_stopping && args.Exception is not null)
        {
            _ = TransitionToFaultedAsync("The microphone stopped unexpectedly.", args.Exception);
        }
    }

    private void OnRenderStopped(object? sender, StoppedEventArgs args)
    {
        if (!_stopping)
        {
            _ = TransitionToFaultedAsync(
                "The selected virtual output stopped.",
                args.Exception);
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs args) =>
        _ = ReconcileDevicesAsync();

    private async Task ReconcileDevicesAsync()
    {
        if (_disposed || _options is null)
        {
            return;
        }

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_options is null)
            {
                return;
            }

            var renderAvailable = IsActive(_options.RenderEndpointId, DataFlow.Render);
            var captureAvailable =
                string.IsNullOrWhiteSpace(_options.CaptureEndpointId) ||
                IsActive(_options.CaptureEndpointId, DataFlow.Capture);

            if (!renderAvailable || !captureAvailable)
            {
                await StopInternalAsync().ConfigureAwait(false);
                SetStatus(
                    AudioEngineState.DeviceUnavailable,
                    "A selected audio endpoint is unavailable. No fallback was selected.");
                return;
            }

            if (Status.State == AudioEngineState.DeviceUnavailable)
            {
                StartInternal(_options);
            }
        }
        catch (Exception exception)
        {
            await StopInternalAsync().ConfigureAwait(false);
            SetStatus(AudioEngineState.Faulted, "Audio endpoint recovery failed.", exception);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private bool IsActive(string endpointId, DataFlow flow)
    {
        foreach (var device in _enumerator.EnumerateAudioEndPoints(
                     flow,
                     DeviceState.Active))
        {
            using (device)
            {
                if (string.Equals(device.ID, endpointId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task TransitionToFaultedAsync(string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
            SetStatus(AudioEngineState.Faulted, message, exception);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private void SetStatus(
        AudioEngineState state,
        string? message = null,
        Exception? exception = null)
    {
        Status = new AudioEngineStatus(state, message, exception);
        StatusChanged?.Invoke(this, Status);
    }

    private void OnMixerPlaybackStateChanged(AudioPlaybackStateChanged change)
    {
        _playbackStateChanges.Enqueue(change);
        if (Interlocked.Exchange(ref _playbackStateDispatchScheduled, 1) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static (WindowsAudioEngine engine) => engine.DispatchPlaybackStateChanges(),
                this,
                preferLocal: false);
        }
    }

    private void DispatchPlaybackStateChanges()
    {
        try
        {
            while (_playbackStateChanges.TryDequeue(out var change))
            {
                var handlers = PlaybackStateChanged;
                if (handlers is null)
                {
                    continue;
                }

                foreach (EventHandler<AudioPlaybackStateChanged> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, change);
                    }
                    catch
                    {
                        // An observer must never take down the audio pipeline.
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _playbackStateDispatchScheduled, 0);
            if (!_playbackStateChanges.IsEmpty &&
                Interlocked.Exchange(ref _playbackStateDispatchScheduled, 1) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static (WindowsAudioEngine engine) => engine.DispatchPlaybackStateChanges(),
                    this,
                    preferLocal: false);
            }
        }
    }
}
