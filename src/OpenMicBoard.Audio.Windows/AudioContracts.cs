using NAudio.Wave;

namespace OpenMicBoard.Audio.Windows;

public enum AudioEndpointKind
{
    Capture,
    Render
}

public enum AudioEngineState
{
    Stopped,
    Starting,
    Running,
    DeviceUnavailable,
    Faulted
}

public enum AudioBufferProfile
{
    LowLatency,
    Balanced,
    Safe
}

public sealed record AudioEndpointInfo(
    string Id,
    string DisplayName,
    AudioEndpointKind Kind,
    int Channels,
    int SampleRate,
    string DeviceName = "");

public sealed record AudioEngineOptions(
    string RenderEndpointId,
    string? CaptureEndpointId,
    AudioBufferProfile BufferProfile = AudioBufferProfile.Balanced,
    float MicrophoneGain = 1f,
    float SoundGain = 1f,
    bool VoiceProcessingEnabled = true,
    bool NoiseGateEnabled = true,
    float NoiseGateThresholdDb = -50f,
    bool MicrophoneMuted = false);

public sealed record AudioEngineStatus(
    AudioEngineState State,
    string? Message = null,
    Exception? Exception = null);

public sealed record AudioPlaybackStateChanged(Guid PlaybackId, bool IsPlaying);

public interface IAudioDeviceCatalog : IDisposable
{
    event EventHandler? DevicesChanged;

    IReadOnlyList<AudioEndpointInfo> GetActiveEndpoints(AudioEndpointKind kind);

    bool GetCaptureMute(string endpointId);

    void SetCaptureMute(string endpointId, bool muted);
}

public interface IAudioEngine : IAsyncDisposable
{
    AudioEngineStatus Status { get; }

    event EventHandler<AudioEngineStatus>? StatusChanged;

    event EventHandler<AudioPlaybackStateChanged>? PlaybackStateChanged;

    Task StartAsync(AudioEngineOptions options, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task PlayAsync(
        Guid playbackId,
        string filePath,
        float volume = 1f,
        CancellationToken cancellationToken = default);

    Task PreviewAsync(
        string filePath,
        string previewEndpointId,
        float volume = 1f,
        CancellationToken cancellationToken = default);

    void StopSound(Guid playbackId);

    void StopAllSounds();

    void UpdateMixSettings(
        float microphoneGain,
        float soundGain,
        bool voiceProcessingEnabled,
        bool noiseGateEnabled,
        float noiseGateThresholdDb,
        bool microphoneMuted);
}

internal interface IAudioFileSource : ISampleProvider, IDisposable;
