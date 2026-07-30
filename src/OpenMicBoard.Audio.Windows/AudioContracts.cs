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
    int SampleRate);

public sealed record AudioEngineOptions(
    string RenderEndpointId,
    string? CaptureEndpointId,
    AudioBufferProfile BufferProfile = AudioBufferProfile.Balanced,
    float MicrophoneGain = 1f,
    float SoundGain = 1f);

public sealed record AudioEngineStatus(
    AudioEngineState State,
    string? Message = null,
    Exception? Exception = null);

public interface IAudioDeviceCatalog : IDisposable
{
    event EventHandler? DevicesChanged;

    IReadOnlyList<AudioEndpointInfo> GetActiveEndpoints(AudioEndpointKind kind);
}

public interface IAudioEngine : IAsyncDisposable
{
    AudioEngineStatus Status { get; }

    event EventHandler<AudioEngineStatus>? StatusChanged;

    Task StartAsync(AudioEngineOptions options, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task PlayAsync(
        Guid soundId,
        string filePath,
        float volume = 1f,
        CancellationToken cancellationToken = default);

    Task PreviewAsync(
        string filePath,
        string previewEndpointId,
        float volume = 1f,
        CancellationToken cancellationToken = default);

    void StopSound(Guid soundId);

    void StopAllSounds();
}

internal interface IAudioFileSource : ISampleProvider, IDisposable;
