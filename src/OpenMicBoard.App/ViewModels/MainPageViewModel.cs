using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenMicBoard.App.Services;
using OpenMicBoard.Audio.Windows;
using OpenMicBoard.Core;
using OpenMicBoard.Infrastructure;

namespace OpenMicBoard.App.ViewModels;

public sealed partial class SoundPadViewModel : ObservableObject
{
    private readonly Func<SoundPadViewModel, Task> _play;
    private readonly Func<SoundPadViewModel, Task> _preview;
    private readonly Action<SoundPadViewModel> _stop;

    public SoundPadViewModel(
        SoundPad pad,
        SoundAsset asset,
        string resolvedFilePath,
        bool isMissing,
        Func<SoundPadViewModel, Task> play,
        Func<SoundPadViewModel, Task> preview,
        Action<SoundPadViewModel> stop)
    {
        Pad = pad;
        Asset = asset;
        ResolvedFilePath = resolvedFilePath;
        IsMissing = isMissing;
        _play = play;
        _preview = preview;
        _stop = stop;
        PlayCommand = new AsyncRelayCommand(() => _play(this));
        PreviewCommand = new AsyncRelayCommand(() => _preview(this));
        StopCommand = new RelayCommand(() => _stop(this));
    }

    public SoundPad Pad { get; }

    public SoundAsset Asset { get; }

    public string ResolvedFilePath { get; }

    public bool IsMissing { get; }

    public string Name => Pad.Name;

    public string Subtitle =>
        IsMissing
            ? "Linked file unavailable"
            : Pad.Hotkey ?? (Asset.Kind == SoundAssetKind.Managed ? "Managed copy" : "Linked file");

    public string Glyph => IsMissing ? "\uE783" : "\uE8D6";

    public IAsyncRelayCommand PlayCommand { get; }

    public IAsyncRelayCommand PreviewCommand { get; }

    public IRelayCommand StopCommand { get; }
}

public sealed record ProfileOption(Guid Id, string Name);

public sealed partial class MainPageViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly Uri ReleasesApi =
        new("https://api.github.com/repos/vounder/OpenMicBoard/releases");
    private readonly IAudioDeviceCatalog _deviceCatalog;
    private readonly IAudioEngine _audioEngine;
    private readonly JsonFileStore<AppSettings> _settingsStore;
    private readonly JsonFileStore<LibraryState> _libraryStore;
    private readonly SoundLibrary _soundLibrary;
    private readonly GitHubHttpTransport _updateTransport;
    private readonly GitHubUpdateChecker _updateChecker;
    private GlobalHotkeyService? _hotkeys;
    private AppSettings _settings = new();
    private LibraryState _library = LibraryState.Empty;
    private bool _initialized;
    private int _deviceRefreshDepth;

    public MainPageViewModel()
    {
        var dataDirectory = StoragePaths.ResolveDataDirectory(AppContext.BaseDirectory);
        _settingsStore = new JsonFileStore<AppSettings>(
            Path.Combine(dataDirectory, "settings.json"));
        _libraryStore = new JsonFileStore<LibraryState>(
            Path.Combine(dataDirectory, "library.json"));
        _soundLibrary = new SoundLibrary(Path.Combine(dataDirectory, "library"));
        _deviceCatalog = new AudioDeviceCatalog();
        _audioEngine = new WindowsAudioEngine(_deviceCatalog);
        _updateTransport = new GitHubHttpTransport();
        _updateChecker = new GitHubUpdateChecker(_updateTransport, ReleasesApi);

        _deviceCatalog.DevicesChanged += (_, _) =>
            App.DispatcherQueue.TryEnqueue(RefreshDevices);
        _audioEngine.StatusChanged += (_, status) =>
            App.DispatcherQueue.TryEnqueue(() => ApplyStatus(status));
    }

    public ObservableCollection<AudioEndpointInfo> RenderEndpoints { get; } = [];

    public ObservableCollection<AudioEndpointInfo> CaptureEndpoints { get; } = [];

    public ObservableCollection<AudioEndpointInfo> PreviewEndpoints { get; } = [];

    public ObservableCollection<SoundPadViewModel> Pads { get; } = [];

    public ObservableCollection<ProfileOption> Profiles { get; } = [];

    public IReadOnlyList<string> BufferProfiles { get; } =
        ["LowLatency", "Balanced", "Safe"];

    [ObservableProperty]
    public partial AudioEndpointInfo? SelectedRenderEndpoint { get; set; }

    [ObservableProperty]
    public partial AudioEndpointInfo? SelectedCaptureEndpoint { get; set; }

    [ObservableProperty]
    public partial AudioEndpointInfo? SelectedPreviewEndpoint { get; set; }

    [ObservableProperty]
    public partial ProfileOption? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Choose a virtual output to begin.";

    [ObservableProperty]
    public partial string StatusGlyph { get; set; } = "\uE946";

    [ObservableProperty]
    public partial bool IsAudioRunning { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial bool Autostart { get; set; }

    [ObservableProperty]
    public partial bool HasSounds { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial string SelectedBufferProfile { get; set; } = "Balanced";

    [ObservableProperty]
    public partial string ActiveProfileTitle { get; set; } = "Default";

    public bool RequiresOnboarding => !_settings.OnboardingCompleted;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        IsBusy = true;
        try
        {
            _settings = await _settingsStore.LoadAsync() ?? new AppSettings();
            _library = await _libraryStore.LoadAsync() ?? LibraryState.Empty;
            CheckForUpdates = _settings.CheckForUpdates;
            Autostart = _settings.Autostart;
            SelectedBufferProfile = _settings.BufferProfile;
            RefreshDevices();
            RebuildProfiles();
            RestoreSelections();
            RebuildPads();
            RebuildHotkeys();

            if (_settings.CheckForUpdates)
            {
                await CheckUpdatesCoreAsync(openReleasePage: false);
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"Local data could not be loaded: {exception.Message}";
            StatusGlyph = "\uEA39";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ImportSoundAsync(string filePath, bool managed)
    {
        IsBusy = true;
        try
        {
            var asset = managed
                ? await _soundLibrary.ImportManagedAsync(filePath)
                : _soundLibrary.Link(filePath);
            var profile = GetActiveProfile();
            var pad = new SoundPad(
                Guid.NewGuid(),
                asset.DisplayName,
                asset.Id,
                Order: profile.Pads.Count);
            var updatedProfile = profile with { Pads = [.. profile.Pads, pad] };
            _library = _library with
            {
                Assets = [.. _library.Assets, asset],
                Profiles = _library.Profiles
                    .Select(item => item.Id == updatedProfile.Id ? updatedProfile : item)
                    .ToArray(),
                ActiveProfileId = updatedProfile.Id
            };
            await _libraryStore.SaveAsync(_library);
            RebuildPads();
            StatusMessage = managed
                ? "Sound copied into the private library."
                : "Sound linked. Moving the original will require relinking.";
            StatusGlyph = "\uE73E";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Import failed: {exception.Message}";
            StatusGlyph = "\uEA39";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleAudioAsync()
    {
        if (IsAudioRunning)
        {
            await _audioEngine.StopAsync();
            return;
        }

        if (SelectedRenderEndpoint is null)
        {
            StatusMessage = "Select the virtual output that feeds your communication app.";
            StatusGlyph = "\uEA39";
            return;
        }

        IsBusy = true;
        try
        {
            await SaveSettingsAsync();
            await _audioEngine.StartAsync(
                new AudioEngineOptions(
                    SelectedRenderEndpoint.Id,
                    SelectedCaptureEndpoint?.Id,
                    ParseBufferProfile(_settings.BufferProfile),
                    _settings.MicrophoneGain,
                    _settings.SoundGain));
        }
        catch (Exception exception)
        {
            StatusMessage = $"Audio start failed: {exception.Message}";
            StatusGlyph = "\uEA39";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void StopAll() => _audioEngine.StopAllSounds();

    [RelayCommand]
    private void RefreshDevices()
    {
        var selectedRenderId = SelectedRenderEndpoint?.Id ?? _settings.RenderEndpointId;
        var selectedCaptureId = SelectedCaptureEndpoint?.Id ?? _settings.CaptureEndpointId;
        var selectedPreviewId = SelectedPreviewEndpoint?.Id ?? _settings.PreviewEndpointId;

        _deviceRefreshDepth++;
        try
        {
            ReplaceCollection(
                RenderEndpoints,
                _deviceCatalog.GetActiveEndpoints(AudioEndpointKind.Render));
            ReplaceCollection(
                CaptureEndpoints,
                _deviceCatalog.GetActiveEndpoints(AudioEndpointKind.Capture));
            ReplaceCollection(
                PreviewEndpoints,
                _deviceCatalog.GetActiveEndpoints(AudioEndpointKind.Render));

            SelectedRenderEndpoint =
                RenderEndpoints.FirstOrDefault(endpoint => endpoint.Id == selectedRenderId);
            SelectedCaptureEndpoint =
                CaptureEndpoints.FirstOrDefault(endpoint => endpoint.Id == selectedCaptureId);
            SelectedPreviewEndpoint =
                PreviewEndpoints.FirstOrDefault(endpoint => endpoint.Id == selectedPreviewId);
        }
        finally
        {
            _deviceRefreshDepth--;
        }
    }

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        var profile = new Profile(
            Guid.NewGuid(),
            $"Profile {_library.Profiles.Count + 1}",
            []);
        _library = _library with
        {
            Profiles = [.. _library.Profiles, profile],
            ActiveProfileId = profile.Id
        };
        await _libraryStore.SaveAsync(_library);
        RebuildProfiles();
        SelectedProfile = Profiles.First(item => item.Id == profile.Id);
    }

    [RelayCommand]
    private Task CheckUpdatesAsync() => CheckUpdatesCoreAsync(openReleasePage: true);

    private async Task CheckUpdatesCoreAsync(bool openReleasePage)
    {
        if (!CheckForUpdates)
        {
            StatusMessage = "Update checks are off. OpenMicBoard remains offline.";
            StatusGlyph = "\uE72E";
            return;
        }

        try
        {
            _settings = _settings with { CheckForUpdates = true };
            await _settingsStore.SaveAsync(_settings);
            var currentVersion = typeof(MainPageViewModel).Assembly.GetName().Version ??
                                 new Version(0, 0);
            var update = await _updateChecker.CheckAsync(_settings, currentVersion);
            if (update is null)
            {
                StatusMessage = "OpenMicBoard is up to date.";
                StatusGlyph = "\uE73E";
                return;
            }

            StatusMessage = $"Version {update.Version} is available on GitHub Releases.";
            StatusGlyph = "\uE895";
            if (openReleasePage)
            {
                Process.Start(new ProcessStartInfo(update.ReleaseUri.ToString())
                {
                    UseShellExecute = true
                });
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"Update check failed: {exception.Message}";
            StatusGlyph = "\uEA39";
        }
    }

    public async Task SetUpdateConsentAsync(bool enabled)
    {
        CheckForUpdates = enabled;
        _settings = _settings with { CheckForUpdates = enabled };
        await _settingsStore.SaveAsync(_settings);
        StatusMessage = enabled
            ? "Update checks enabled. GitHub receives normal connection metadata."
            : "Update checks disabled. OpenMicBoard remains offline.";
        StatusGlyph = enabled ? "\uE8D7" : "\uE72E";
    }

    public async Task CompleteOnboardingAsync(bool enableUpdateChecks)
    {
        CheckForUpdates = enableUpdateChecks;
        _settings = _settings with
        {
            CheckForUpdates = enableUpdateChecks,
            OnboardingCompleted = true
        };
        await _settingsStore.SaveAsync(_settings);
        StatusMessage = enableUpdateChecks
            ? "Ready. Optional update checks are enabled."
            : "Ready. OpenMicBoard will stay offline.";
        StatusGlyph = "\uE73E";
    }

    public async Task SaveDeviceSelectionAsync()
    {
        if (_deviceRefreshDepth == 0)
        {
            await SaveSettingsAsync();
        }
    }

    public void AttachGlobalHotkeys(GlobalHotkeyService hotkeys)
    {
        _hotkeys = hotkeys;
        _hotkeys.Pressed += OnHotkeyPressed;
        RebuildHotkeys();
    }

    public async Task<string?> SetHotkeyAsync(Guid padId, string? chord)
    {
        chord = string.IsNullOrWhiteSpace(chord) ? null : chord.Trim();
        if (chord is not null &&
            !GlobalHotkeyService.TryParseChord(chord, out _, out _, out var parseError))
        {
            return parseError;
        }

        var profile = GetActiveProfile();
        var previousPad = profile.Pads.First(item => item.Id == padId);
        var updatedPad = previousPad with { Hotkey = chord };
        var updatedProfile = profile with
        {
            Pads = profile.Pads
                .Select(item => item.Id == padId ? updatedPad : item)
                .ToArray()
        };
        _library = _library with
        {
            Profiles = _library.Profiles
                .Select(item => item.Id == profile.Id ? updatedProfile : item)
                .ToArray()
        };

        var registrationError = RebuildHotkeys();
        if (registrationError is not null)
        {
            updatedProfile = updatedProfile with
            {
                Pads = updatedProfile.Pads
                    .Select(item => item.Id == padId ? previousPad : item)
                    .ToArray()
            };
            _library = _library with
            {
                Profiles = _library.Profiles
                    .Select(item => item.Id == profile.Id ? updatedProfile : item)
                    .ToArray()
            };
            RebuildHotkeys();
            return registrationError;
        }

        await _libraryStore.SaveAsync(_library);
        RebuildPads();
        return null;
    }

    public async Task SetAutostartAsync(bool enabled)
    {
        try
        {
            AutostartService.SetEnabled(enabled);
            Autostart = enabled;
            _settings = _settings with { Autostart = enabled };
            await _settingsStore.SaveAsync(_settings);
            StatusMessage = enabled
                ? "OpenMicBoard will start minimized when you sign in."
                : "Autostart disabled.";
            StatusGlyph = "\uE73E";
        }
        catch (Exception exception)
        {
            Autostart = !enabled;
            StatusMessage = $"Autostart could not be changed: {exception.Message}";
            StatusGlyph = "\uEA39";
        }
    }

    public async Task SetBufferProfileAsync(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile) ||
            !BufferProfiles.Contains(profile, StringComparer.Ordinal))
        {
            return;
        }

        SelectedBufferProfile = profile;
        _settings = _settings with { BufferProfile = profile };
        await _settingsStore.SaveAsync(_settings);
        StatusMessage = profile switch
        {
            "LowLatency" => "Low latency selected. Use Balanced if audio crackles.",
            "Safe" => "Safe buffering selected for less stable hardware.",
            _ => "Balanced buffering selected."
        };
        StatusGlyph = "\uE73E";
    }

    public async ValueTask DisposeAsync()
    {
        if (_hotkeys is not null)
        {
            _hotkeys.Pressed -= OnHotkeyPressed;
            _hotkeys.Dispose();
        }

        await _audioEngine.DisposeAsync();
        _deviceCatalog.Dispose();
        _updateTransport.Dispose();
    }

    partial void OnSelectedProfileChanged(ProfileOption? value)
    {
        if (value is null || _library.ActiveProfileId == value.Id)
        {
            return;
        }

        _audioEngine.StopAllSounds();
        _library = _library with { ActiveProfileId = value.Id };
        ActiveProfileTitle = value.Name;
        RebuildPads();
        RebuildHotkeys();
        _ = _libraryStore.SaveAsync(_library);
    }

    private async Task PlayAsync(SoundPadViewModel pad)
    {
        if (pad.IsMissing)
        {
            StatusMessage = "The linked sound file is unavailable.";
            StatusGlyph = "\uEA39";
            return;
        }

        try
        {
            await _audioEngine.PlayAsync(
                pad.Asset.Id,
                pad.ResolvedFilePath,
                pad.Pad.Volume);
            StatusMessage = $"Playing {pad.Name}";
            StatusGlyph = "\uE8D6";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            StatusGlyph = "\uEA39";
        }
    }

    private void OnHotkeyPressed(object? sender, Guid padId)
    {
        var pad = Pads.FirstOrDefault(item => item.Pad.Id == padId);
        if (pad is not null)
        {
            _ = PlayAsync(pad);
        }
    }

    private string? RebuildHotkeys()
    {
        if (_hotkeys is null)
        {
            return null;
        }

        _hotkeys.Clear();
        foreach (var pad in GetActiveProfile().Pads)
        {
            if (string.IsNullOrWhiteSpace(pad.Hotkey))
            {
                continue;
            }

            if (!_hotkeys.TryRegister(pad.Hotkey, pad.Id, out var error))
            {
                return $"{pad.Name}: {error}";
            }
        }

        return null;
    }

    private async Task PreviewAsync(SoundPadViewModel pad)
    {
        if (SelectedPreviewEndpoint is null)
        {
            StatusMessage = "Choose a preview speaker or headset first.";
            StatusGlyph = "\uEA39";
            return;
        }

        try
        {
            await _audioEngine.PreviewAsync(
                pad.ResolvedFilePath,
                SelectedPreviewEndpoint.Id,
                pad.Pad.Volume);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Preview failed: {exception.Message}";
            StatusGlyph = "\uEA39";
        }
    }

    private void ApplyStatus(AudioEngineStatus status)
    {
        IsAudioRunning = status.State == AudioEngineState.Running;
        StatusMessage = status.State switch
        {
            AudioEngineState.Running => "Audio route is live.",
            AudioEngineState.DeviceUnavailable =>
                status.Message ?? "A selected endpoint is unavailable.",
            AudioEngineState.Faulted => status.Message ?? "Audio stopped after an error.",
            AudioEngineState.Stopped => "Audio route is stopped.",
            _ => "Starting audio route..."
        };
        StatusGlyph = status.State switch
        {
            AudioEngineState.Running => "\uE73E",
            AudioEngineState.Starting => "\uE895",
            AudioEngineState.Stopped => "\uE72E",
            _ => "\uEA39"
        };
    }

    private void RestoreSelections()
    {
        SelectedRenderEndpoint =
            RenderEndpoints.FirstOrDefault(item => item.Id == _settings.RenderEndpointId);
        SelectedCaptureEndpoint =
            CaptureEndpoints.FirstOrDefault(item => item.Id == _settings.CaptureEndpointId);
        SelectedPreviewEndpoint =
            PreviewEndpoints.FirstOrDefault(item => item.Id == _settings.PreviewEndpointId);
    }

    private void RebuildProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _library.Profiles)
        {
            Profiles.Add(new ProfileOption(profile.Id, profile.Name));
        }

        var selectedId = _library.ActiveProfileId ?? _library.Profiles.First().Id;
        SelectedProfile = Profiles.FirstOrDefault(item => item.Id == selectedId) ??
                          Profiles.FirstOrDefault();
        ActiveProfileTitle = SelectedProfile?.Name ?? "Default";
    }

    private void RebuildPads()
    {
        Pads.Clear();
        var profile = GetActiveProfile();
        foreach (var pad in profile.Pads.OrderBy(item => item.Order))
        {
            var asset = _library.Assets.FirstOrDefault(item => item.Id == pad.SoundAssetId);
            if (asset is null)
            {
                continue;
            }

            Pads.Add(
                new SoundPadViewModel(
                    pad,
                    asset,
                    _soundLibrary.ResolvePath(asset),
                    _soundLibrary.IsMissing(asset),
                    PlayAsync,
                    PreviewAsync,
                    item => _audioEngine.StopSound(item.Asset.Id)));
        }

        HasSounds = Pads.Count > 0;
        IsEmpty = !HasSounds;
    }

    private Profile GetActiveProfile()
    {
        var id = _library.ActiveProfileId ?? _library.Profiles.First().Id;
        return _library.Profiles.First(profile => profile.Id == id);
    }

    private async Task SaveSettingsAsync()
    {
        _settings = _settings with
        {
            RenderEndpointId = SelectedRenderEndpoint?.Id,
            CaptureEndpointId = SelectedCaptureEndpoint?.Id,
            PreviewEndpointId = SelectedPreviewEndpoint?.Id,
            BufferProfile = SelectedBufferProfile,
            CheckForUpdates = CheckForUpdates
        };
        await _settingsStore.SaveAsync(_settings);
    }

    private static AudioBufferProfile ParseBufferProfile(string value) =>
        Enum.TryParse<AudioBufferProfile>(value, true, out var profile)
            ? profile
            : AudioBufferProfile.Balanced;

    private static void ReplaceCollection<T>(
        ObservableCollection<T> target,
        IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
