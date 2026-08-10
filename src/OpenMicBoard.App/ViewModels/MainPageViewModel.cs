using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenMicBoard.App.Services;
using OpenMicBoard.Audio.Windows;
using OpenMicBoard.Core;
using OpenMicBoard.Infrastructure;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

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
        Action<SoundPadViewModel> stop,
        bool isPlaying)
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
        StopCommand = new RelayCommand(() => _stop(this), () => IsPlaying);
        IsPlaying = isPlaying;
    }

    public SoundPad Pad { get; }

    public SoundAsset Asset { get; }

    public string ResolvedFilePath { get; }

    public bool IsMissing { get; }

    public string Name => Pad.Name;

    public string Subtitle => IsMissing ? "Linked file unavailable" :
        (Asset.Kind == SoundAssetKind.Managed ? "Managed copy" : "Linked file");

    public string ShortcutLabel => string.IsNullOrWhiteSpace(Pad.Hotkey) ? "Set shortcut" : Pad.Hotkey;

    public string Glyph => IsMissing ? "\uE783" : SoundPadIconCatalog.Resolve(Pad.IconToken).Glyph;

    public Brush AccentBrush => new SolidColorBrush(ParseColor(Pad.ColorToken));
    public Brush IconForeground => new SolidColorBrush(MainPageViewModel.GetContrastingTextColor(ParseColor(Pad.ColorToken)));

    [ObservableProperty] public partial bool IsPlaying { get; set; }

    public IAsyncRelayCommand PlayCommand { get; }

    public IAsyncRelayCommand PreviewCommand { get; }

    public IRelayCommand StopCommand { get; }

    partial void OnIsPlayingChanged(bool value) => StopCommand.NotifyCanExecuteChanged();

    private static Color ParseColor(string? value) => MainPageViewModel.ParsePadColor(value);
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
    private readonly HashSet<Guid> _playingPadIds = [];
    private readonly object _previewPlaybackLock = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _previewPlaybackCancellations = [];

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
        _audioEngine.PlaybackStateChanged += (_, state) =>
            App.DispatcherQueue.TryEnqueue(() => ApplyPlaybackState(state));
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

    [ObservableProperty]
    public partial double MicrophoneBoostDb { get; set; }

    [ObservableProperty]
    public partial double SoundBoostDb { get; set; }

    [ObservableProperty]
    public partial bool VoiceProcessingEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool NoiseGateEnabled { get; set; } = true;

    [ObservableProperty]
    public partial double NoiseGateThresholdDb { get; set; } = -50;

    [ObservableProperty]
    public partial bool IsMicrophoneMuted { get; set; }

    public string MicrophoneBoostLabel => FormatDecibels(MicrophoneBoostDb);

    public string SoundBoostLabel => FormatDecibels(SoundBoostDb);

    public string NoiseGateThresholdLabel => $"{NoiseGateThresholdDb:0} dB";

    public string MicrophoneMuteLabel =>
        IsMicrophoneMuted ? "Unmute mic" : "Mute mic";

    public string MicrophoneMuteGlyph =>
        IsMicrophoneMuted ? "\uE74F" : "\uE720";

    public string MicrophoneMuteToolTip =>
        IsMicrophoneMuted
            ? "Unmute the selected Windows microphone for OpenMicBoard and other apps"
            : "Mute the selected Windows microphone for OpenMicBoard and other apps";

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
            MicrophoneBoostDb = LinearToDb(_settings.MicrophoneGain);
            SoundBoostDb = LinearToDb(_settings.SoundGain);
            VoiceProcessingEnabled = _settings.VoiceProcessingEnabled;
            NoiseGateEnabled = _settings.NoiseGateEnabled;
            NoiseGateThresholdDb = _settings.NoiseGateThresholdDb;
            IsMicrophoneMuted = _settings.MicrophoneMuted;
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
            CancelAllPreviewPlayback();
            await _audioEngine.StopAsync();
            return;
        }

        await StartAudioRouteAsync();
    }

    private async Task<bool> StartAudioRouteAsync()
    {
        if (SelectedRenderEndpoint is null)
        {
            StatusMessage = "Select the virtual output that feeds your communication app.";
            StatusGlyph = "\uEA39";
            return false;
        }

        if (BelongsToSameDevice(SelectedPreviewEndpoint, SelectedRenderEndpoint) ||
            BelongsToSameDevice(SelectedCaptureEndpoint, SelectedRenderEndpoint))
        {
            StatusMessage =
                $"“{SelectedRenderEndpoint.DisplayName}” belongs to the same physical device as your " +
                "microphone or private preview. Install and select a virtual audio cable instead.";
            StatusGlyph = "\uEA39";
            return false;
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
                    _settings.SoundGain,
                    _settings.VoiceProcessingEnabled,
                    _settings.NoiseGateEnabled,
                    _settings.NoiseGateThresholdDb,
                    _settings.MicrophoneMuted));
            return true;
        }
        catch (Exception exception)
        {
            StatusMessage = $"Audio start failed: {exception.Message}";
            StatusGlyph = "\uEA39";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void StopAll()
    {
        StopAllPlayback();
    }

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

    public string SuggestedProfileName
    {
        get
        {
            for (var index = 1; ; index++)
            {
                var candidate = $"Profile {index}";
                if (!_library.Profiles.Any(x => string.Equals(x.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
            }
        }
    }

    public async Task<string?> AddProfileAsync(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) return "Profile names must be 1–80 characters.";
        if (_library.Profiles.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return "A profile with that name already exists.";
        var profile = new Profile(
            Guid.NewGuid(),
            name,
            []);
        var candidateLibrary = _library with
        {
            Profiles = [.. _library.Profiles, profile],
            ActiveProfileId = profile.Id
        };
        try
        {
            await _libraryStore.SaveAsync(candidateLibrary);
        }
        catch (Exception exception)
        {
            return $"Profile could not be saved: {exception.Message}";
        }

        _library = candidateLibrary;
        StopAllPlayback();
        _playingPadIds.Clear();
        RebuildProfiles();
        RebuildPads();
        RebuildHotkeys();
        return null;
    }

    public async Task<string?> EditPadAsync(Guid padId, string? name, string? iconToken, string? colorToken)
    {
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) return "Pad names must be 1–80 characters.";
        var profile = GetActiveProfile();
        var pad = profile.Pads.FirstOrDefault(x => x.Id == padId);
        if (pad is null) return "The sound pad is no longer in this profile.";
        var updated = pad with { Name = name, IconToken = SoundPadIconCatalog.Resolve(iconToken).Token, ColorToken = NormalizePadColor(colorToken) };
        var updatedProfile = profile with { Pads = profile.Pads.Select(x => x.Id == padId ? updated : x).ToArray() };
        var candidateLibrary = _library with
        {
            Profiles = _library.Profiles
                .Select(x => x.Id == profile.Id ? updatedProfile : x)
                .ToArray()
        };
        try
        {
            await _libraryStore.SaveAsync(candidateLibrary);
        }
        catch (Exception exception)
        {
            return $"Sound pad could not be saved: {exception.Message}";
        }

        _library = candidateLibrary;
        RebuildPads();
        return null;
    }

    public async Task<string?> RemovePadAsync(Guid padId)
    {
        var profile = GetActiveProfile();
        var pad = profile.Pads.FirstOrDefault(item => item.Id == padId);
        if (pad is null)
        {
            return "The sound pad is no longer in this profile.";
        }

        var remainingPads = profile.Pads
            .Where(item => item.Id != padId)
            .Select((item, index) => item with { Order = index })
            .ToArray();
        var updatedProfile = profile with { Pads = remainingPads };
        var candidateLibrary = _library with
        {
            Profiles = _library.Profiles
                .Select(item => item.Id == profile.Id ? updatedProfile : item)
                .ToArray()
        };

        try
        {
            await _libraryStore.SaveAsync(candidateLibrary);
        }
        catch (Exception exception)
        {
            return $"Sound pad could not be removed: {exception.Message}";
        }

        StopPadPlayback(padId);
        _playingPadIds.Remove(padId);
        _library = candidateLibrary;

        var asset = pad.SoundAssetId is Guid assetId
            ? _library.Assets.FirstOrDefault(item => item.Id == assetId)
            : null;
        var assetStillUsed = asset is not null &&
            _library.Profiles
                .SelectMany(item => item.Pads)
                .Any(item => item.SoundAssetId == asset.Id);
        var cleanupWarning = string.Empty;
        if (asset is not null && !assetStillUsed)
        {
            try
            {
                _soundLibrary.DeleteManaged(asset);
                var compactedLibrary = _library with
                {
                    Assets = _library.Assets
                        .Where(item => item.Id != asset.Id)
                        .ToArray()
                };
                await _libraryStore.SaveAsync(compactedLibrary);
                _library = compactedLibrary;
            }
            catch (Exception exception)
            {
                cleanupWarning =
                    $" The unused private copy could not be deleted: {exception.Message}";
            }
        }

        RebuildPads();
        RebuildHotkeys();
        StatusMessage = $"Removed {pad.Name} from {profile.Name}.{cleanupWarning}";
        StatusGlyph = string.IsNullOrEmpty(cleanupWarning) ? "\uE74D" : "\uEA39";
        return null;
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
            SynchronizeMicrophoneMuteState();
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
        if (chord is not null)
        {
            if (!HotkeyChordParser.TryParse(chord, out var parsed, out var parseError))
            {
                return parseError;
            }

            chord = parsed.CanonicalChord;
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

    [RelayCommand]
    private async Task ApplyMixSettingsAsync()
    {
        var microphoneGain = DbToLinear((float)Math.Clamp(MicrophoneBoostDb, -12, 12));
        var soundGain = DbToLinear((float)Math.Clamp(SoundBoostDb, -12, 12));
        var gateThreshold = (float)Math.Clamp(NoiseGateThresholdDb, -70, -25);
        _settings = _settings with
        {
            MicrophoneGain = microphoneGain,
            SoundGain = soundGain,
            VoiceProcessingEnabled = VoiceProcessingEnabled,
            NoiseGateEnabled = NoiseGateEnabled,
            NoiseGateThresholdDb = gateThreshold,
            MicrophoneMuted = IsMicrophoneMuted
        };
        _audioEngine.UpdateMixSettings(
            microphoneGain,
            soundGain,
            VoiceProcessingEnabled,
            NoiseGateEnabled,
            gateThreshold,
            IsMicrophoneMuted);
        await _settingsStore.SaveAsync(_settings);
        StatusMessage = IsAudioRunning
            ? "Mix processing applied live."
            : "Mix processing saved for the next route start.";
        StatusGlyph = "\uE73E";
    }

    [RelayCommand]
    private async Task ToggleMicrophoneMuteAsync()
    {
        if (SelectedCaptureEndpoint is null)
        {
            StatusMessage = "Choose the microphone you want to mute first.";
            StatusGlyph = "\uEA39";
            return;
        }

        try
        {
            var muted = !_deviceCatalog.GetCaptureMute(SelectedCaptureEndpoint.Id);
            _deviceCatalog.SetCaptureMute(SelectedCaptureEndpoint.Id, muted);
            IsMicrophoneMuted = muted;
            _settings = _settings with { MicrophoneMuted = muted };
            _audioEngine.UpdateMixSettings(
                DbToLinear((float)Math.Clamp(MicrophoneBoostDb, -12, 12)),
                DbToLinear((float)Math.Clamp(SoundBoostDb, -12, 12)),
                VoiceProcessingEnabled,
                NoiseGateEnabled,
                (float)Math.Clamp(NoiseGateThresholdDb, -70, -25),
                muted);
            await _settingsStore.SaveAsync(_settings);
            StatusMessage = muted
                ? "Windows microphone muted. Soundboard playback remains active."
                : "Windows microphone unmuted.";
            StatusGlyph = muted ? "\uE74F" : "\uE720";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Microphone mute failed: {exception.Message}";
            StatusGlyph = "\uEA39";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hotkeys is not null)
        {
            _hotkeys.Pressed -= OnHotkeyPressed;
            _hotkeys.Dispose();
        }
        StopAllPlayback();
        _playingPadIds.Clear();
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

        StopAllPlayback();
        _playingPadIds.Clear();
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

        var isMonitored = StartPrivateMonitor(pad);
        try
        {
            if (!IsAudioRunning && !await StartAudioRouteAsync())
            {
                if (isMonitored)
                {
                    StatusMessage += " The clip is playing on private preview only.";
                }

                return;
            }

            await _audioEngine.PlayAsync(
                pad.Pad.Id,
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

    public Task PlayPadAsync(SoundPadViewModel pad) => PlayAsync(pad);

    private bool StartPrivateMonitor(SoundPadViewModel pad)
    {
        if (SelectedPreviewEndpoint is null ||
            SelectedPreviewEndpoint.Id == SelectedRenderEndpoint?.Id)
        {
            return false;
        }

        _ = MonitorSoundAsync(pad);
        return true;
    }

    private async Task MonitorSoundAsync(SoundPadViewModel pad)
    {
        using var cancellation = BeginPreviewPlayback(pad.Pad.Id);
        try
        {
            await _audioEngine.PreviewAsync(
                pad.ResolvedFilePath,
                SelectedPreviewEndpoint!.Id,
                pad.Pad.Volume,
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusMessage = $"Private preview failed: {exception.Message}";
            StatusGlyph = "\uEA39";
        }
        finally
        {
            CompletePreviewPlayback(pad.Pad.Id, cancellation);
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

        using var cancellation = BeginPreviewPlayback(pad.Pad.Id);
        try
        {
            await _audioEngine.PreviewAsync(
                pad.ResolvedFilePath,
                SelectedPreviewEndpoint.Id,
                pad.Pad.Volume,
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusMessage = $"Preview failed: {exception.Message}";
            StatusGlyph = "\uEA39";
        }
        finally
        {
            CompletePreviewPlayback(pad.Pad.Id, cancellation);
        }
    }

    public Task PreviewPadAsync(SoundPadViewModel pad) => PreviewAsync(pad);

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
        if (status.State != AudioEngineState.Running)
        {
            _playingPadIds.Clear();
            foreach (var pad in Pads) pad.IsPlaying = false;
        }
    }

    private void ApplyPlaybackState(AudioPlaybackStateChanged state)
    {
        if (state.IsPlaying) _playingPadIds.Add(state.PlaybackId); else _playingPadIds.Remove(state.PlaybackId);
        var pad = Pads.FirstOrDefault(x => x.Pad.Id == state.PlaybackId);
        if (pad is not null) pad.IsPlaying = state.IsPlaying;
    }

    private void RestoreSelections()
    {
        SelectedRenderEndpoint =
            RenderEndpoints.FirstOrDefault(item => item.Id == _settings.RenderEndpointId);
        SelectedCaptureEndpoint =
            CaptureEndpoints.FirstOrDefault(item => item.Id == _settings.CaptureEndpointId);
        SelectedPreviewEndpoint =
            PreviewEndpoints.FirstOrDefault(item => item.Id == _settings.PreviewEndpointId);
        SynchronizeMicrophoneMuteState();
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
                    item => StopPadPlayback(item.Pad.Id),
                    _playingPadIds.Contains(pad.Id)));
        }

        HasSounds = Pads.Count > 0;
        IsEmpty = !HasSounds;
    }

    private void StopPadPlayback(Guid padId)
    {
        _audioEngine.StopSound(padId);
        CancelPreviewPlayback(padId);
    }

    private void StopAllPlayback()
    {
        _audioEngine.StopAllSounds();
        CancelAllPreviewPlayback();
    }

    private CancellationTokenSource BeginPreviewPlayback(Guid padId)
    {
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_previewPlaybackLock)
        {
            _previewPlaybackCancellations.TryGetValue(padId, out previous);
            _previewPlaybackCancellations[padId] = cancellation;
        }

        CancelAndDispose(previous);
        return cancellation;
    }

    private void CompletePreviewPlayback(Guid padId, CancellationTokenSource cancellation)
    {
        lock (_previewPlaybackLock)
        {
            if (_previewPlaybackCancellations.TryGetValue(padId, out var current) &&
                ReferenceEquals(current, cancellation))
            {
                _previewPlaybackCancellations.Remove(padId);
            }
        }
    }

    private void CancelPreviewPlayback(Guid padId)
    {
        CancellationTokenSource? cancellation;
        lock (_previewPlaybackLock)
        {
            if (!_previewPlaybackCancellations.Remove(padId, out cancellation))
            {
                return;
            }
        }

        CancelAndDispose(cancellation);
    }

    private void CancelAllPreviewPlayback()
    {
        CancellationTokenSource[] cancellations;
        lock (_previewPlaybackLock)
        {
            cancellations = [.. _previewPlaybackCancellations.Values];
            _previewPlaybackCancellations.Clear();
        }

        foreach (var cancellation in cancellations)
        {
            CancelAndDispose(cancellation);
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
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
            CheckForUpdates = CheckForUpdates,
            MicrophoneGain = DbToLinear((float)Math.Clamp(MicrophoneBoostDb, -12, 12)),
            SoundGain = DbToLinear((float)Math.Clamp(SoundBoostDb, -12, 12)),
            VoiceProcessingEnabled = VoiceProcessingEnabled,
            NoiseGateEnabled = NoiseGateEnabled,
            NoiseGateThresholdDb = (float)Math.Clamp(NoiseGateThresholdDb, -70, -25),
            MicrophoneMuted = IsMicrophoneMuted
        };
        await _settingsStore.SaveAsync(_settings);
    }

    private static AudioBufferProfile ParseBufferProfile(string value) =>
        Enum.TryParse<AudioBufferProfile>(value, true, out var profile)
            ? profile
            : AudioBufferProfile.Balanced;

    public static string NormalizePadColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "Mint", StringComparison.OrdinalIgnoreCase)) return "#9FE7D7";
        var text = value.Trim();
        return text.Length == 7 && text[0] == '#' && uint.TryParse(text[1..], System.Globalization.NumberStyles.HexNumber, null, out _) ? text.ToUpperInvariant() : "#9FE7D7";
    }

    public static Color ParsePadColor(string? value)
    {
        var text = NormalizePadColor(value);
        return Color.FromArgb(255, Convert.ToByte(text[1..3], 16), Convert.ToByte(text[3..5], 16), Convert.ToByte(text[5..7], 16));
    }

    public static Color GetContrastingTextColor(Color background)
    {
        static double Linearize(byte component)
        {
            var channel = component / 255d;
            return channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        var luminance =
            0.2126 * Linearize(background.R) +
            0.7152 * Linearize(background.G) +
            0.0722 * Linearize(background.B);
        var whiteContrast = 1.05 / (luminance + 0.05);
        var blackContrast = (luminance + 0.05) / 0.05;
        return whiteContrast >= blackContrast
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 0, 0, 0);
    }

    partial void OnMicrophoneBoostDbChanged(double value) =>
        OnPropertyChanged(nameof(MicrophoneBoostLabel));

    partial void OnSoundBoostDbChanged(double value) =>
        OnPropertyChanged(nameof(SoundBoostLabel));

    partial void OnNoiseGateThresholdDbChanged(double value) =>
        OnPropertyChanged(nameof(NoiseGateThresholdLabel));

    partial void OnIsMicrophoneMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(MicrophoneMuteLabel));
        OnPropertyChanged(nameof(MicrophoneMuteGlyph));
        OnPropertyChanged(nameof(MicrophoneMuteToolTip));
    }

    private void SynchronizeMicrophoneMuteState()
    {
        if (SelectedCaptureEndpoint is null)
        {
            IsMicrophoneMuted = false;
            return;
        }

        try
        {
            IsMicrophoneMuted = _deviceCatalog.GetCaptureMute(SelectedCaptureEndpoint.Id);
        }
        catch
        {
            // Device refreshes can race an endpoint removal. The next notification retries.
        }
    }

    private static bool BelongsToSameDevice(
        AudioEndpointInfo? left,
        AudioEndpointInfo? right) =>
        left is not null &&
        right is not null &&
        !string.IsNullOrWhiteSpace(left.DeviceName) &&
        string.Equals(
            left.DeviceName,
            right.DeviceName,
            StringComparison.OrdinalIgnoreCase);

    private static float DbToLinear(float decibels) =>
        MathF.Pow(10f, decibels / 20f);

    private static double LinearToDb(float linear) =>
        Math.Clamp(20d * Math.Log10(Math.Max(linear, 0.000_001f)), -12d, 12d);

    private static string FormatDecibels(double decibels) =>
        $"{decibels:+0;-0;0} dB";

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
