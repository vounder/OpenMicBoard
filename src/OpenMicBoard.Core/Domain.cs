namespace OpenMicBoard.Core;

/// <summary>Contract implemented by persisted v1 records.</summary>
public interface ISchemaVersioned { int SchemaVersion { get; } }

public enum SoundAssetKind { Managed, Linked }

/// <summary>A playable sound. Managed assets use a private library file; linked assets retain their external path.</summary>
public sealed record SoundAsset(
    Guid Id,
    SoundAssetKind Kind,
    string FilePath,
    string DisplayName,
    int SchemaVersion = 1,
    TimeSpan? Duration = null,
    string? ContentHash = null) : ISchemaVersioned;

/// <summary>A user-configurable soundboard pad.</summary>
public sealed record SoundPad(
    Guid Id,
    string Name,
    Guid? SoundAssetId,
    string? Hotkey = null,
    int SchemaVersion = 1,
    float Volume = 1f,
    string ColorToken = "Mint",
    int Order = 0,
    string IconToken = "Audio") : ISchemaVersioned;

/// <summary>A named collection of pads.</summary>
public sealed record Profile(Guid Id, string Name, IReadOnlyList<SoundPad> Pads, int SchemaVersion = 1) : ISchemaVersioned;

public enum UpdateChannel { Stable, Prerelease }

/// <summary>Application preferences. Update checks are disabled unless explicitly enabled.</summary>
public sealed record AppSettings(
    bool CheckForUpdates = false,
    UpdateChannel UpdateChannel = UpdateChannel.Stable,
    int SchemaVersion = 1,
    string? RenderEndpointId = null,
    string? CaptureEndpointId = null,
    string? PreviewEndpointId = null,
    string BufferProfile = "Balanced",
    string Language = "system",
    bool Autostart = false,
    bool OnboardingCompleted = false,
    float MicrophoneGain = 1f,
    float SoundGain = 1f,
    bool VoiceProcessingEnabled = true,
    bool NoiseGateEnabled = true,
    float NoiseGateThresholdDb = -50f,
    bool MicrophoneMuted = false) : ISchemaVersioned;

/// <summary>The complete local library; managed sound bytes live beside this index.</summary>
public sealed record LibraryState(
    IReadOnlyList<SoundAsset> Assets,
    IReadOnlyList<Profile> Profiles,
    Guid? ActiveProfileId = null,
    int SchemaVersion = 1) : ISchemaVersioned
{
    public static LibraryState Empty { get; } =
        new([], [new Profile(Guid.NewGuid(), "Default", [])]);
}

/// <summary>Small injectable transport used by the privacy-respecting update checker.</summary>
public interface IUpdateTransport
{
    Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed record UpdateInfo(string Version, Uri ReleaseUri, bool IsPrerelease);

/// <summary>Checks GitHub releases only after the user has opted in; it never downloads anything.</summary>
public sealed class GitHubUpdateChecker(IUpdateTransport transport, Uri releasesApi)
{
    public async Task<UpdateInfo?> CheckAsync(AppSettings settings, Version currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.CheckForUpdates) return null;
        var json = await transport.GetStringAsync(releasesApi, cancellationToken).ConfigureAwait(false);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            var prerelease = release.GetProperty("prerelease").GetBoolean();
            if (settings.UpdateChannel == UpdateChannel.Stable && prerelease) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= currentVersion) continue;
            var url = new Uri(release.GetProperty("html_url").GetString()!, UriKind.Absolute);
            return new UpdateInfo(version.ToString(), url, prerelease);
        }
        return null;
    }
}
