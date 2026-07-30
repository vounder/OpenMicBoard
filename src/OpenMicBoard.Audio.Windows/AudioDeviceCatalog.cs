using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace OpenMicBoard.Audio.Windows;

public sealed class AudioDeviceCatalog : IAudioDeviceCatalog, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _disposed;

    public AudioDeviceCatalog()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioEndpointInfo> GetActiveEndpoints(AudioEndpointKind kind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var flow = kind == AudioEndpointKind.Capture ? DataFlow.Capture : DataFlow.Render;

        var endpoints = new List<AudioEndpointInfo>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(
                     flow,
                     DeviceState.Active))
        {
            using (device)
            {
                var format = device.AudioClient.MixFormat;
                endpoints.Add(new AudioEndpointInfo(
                    device.ID,
                    device.FriendlyName,
                    kind,
                    format.Channels,
                    format.SampleRate));
            }
        }

        return endpoints
            .OrderBy(endpoint => endpoint.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => NotifyChanged();

    public void OnDeviceAdded(string pwstrDeviceId) => NotifyChanged();

    public void OnDeviceRemoved(string deviceId) => NotifyChanged();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) =>
        NotifyChanged();

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => NotifyChanged();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
    }

    private void NotifyChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}
