using OpenMicBoard.Core;
using OpenMicBoard.Infrastructure;

namespace OpenMicBoard.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omb-" + Guid.NewGuid());
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task Schema_round_trips_and_rejects_newer_versions()
    {
        var path = System.IO.Path.Combine(root, "settings.json"); var store = new JsonFileStore<AppSettings>(path);
        await store.SaveAsync(new AppSettings(true, UpdateChannel.Prerelease));
        Assert.Equal(UpdateChannel.Prerelease, (await store.LoadAsync())!.UpdateChannel);
        await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveAsync(new AppSettings(SchemaVersion: 2)));
    }

    [Fact]
    public async Task Legacy_settings_get_safe_voice_processing_defaults_and_mix_round_trips()
    {
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(path, """
        {
          "CheckForUpdates": false,
          "SchemaVersion": 1,
          "MicrophoneGain": 1,
          "SoundGain": 1
        }
        """);
        var store = new JsonFileStore<AppSettings>(path);

        var legacy = (await store.LoadAsync())!;
        Assert.True(legacy.VoiceProcessingEnabled);
        Assert.True(legacy.NoiseGateEnabled);
        Assert.Equal(-50f, legacy.NoiseGateThresholdDb);
        Assert.False(legacy.MicrophoneMuted);

        var customized = legacy with
        {
            MicrophoneGain = 1.5f,
            SoundGain = 2f,
            VoiceProcessingEnabled = false,
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -44f,
            MicrophoneMuted = true
        };
        await store.SaveAsync(customized);
        var reloaded = (await store.LoadAsync())!;
        Assert.Equal(1.5f, reloaded.MicrophoneGain);
        Assert.Equal(2f, reloaded.SoundGain);
        Assert.False(reloaded.VoiceProcessingEnabled);
        Assert.True(reloaded.NoiseGateEnabled);
        Assert.Equal(-44f, reloaded.NoiseGateThresholdDb);
        Assert.True(reloaded.MicrophoneMuted);
    }

    [Fact]
    public async Task Corrupt_primary_recovers_backup()
    {
        var path = System.IO.Path.Combine(root, "settings.json"); var store = new JsonFileStore<AppSettings>(path);
        await store.SaveAsync(new AppSettings(true)); await store.SaveAsync(new AppSettings(false));
        await File.WriteAllTextAsync(path, "{");
        Assert.True((await store.LoadAsync())!.CheckForUpdates);
    }
    [Fact]
    public async Task Legacy_library_without_icon_token_loads_with_defaults_and_preserves_data()
    {
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "library.json");
        var assetId = Guid.NewGuid(); var profileId = Guid.NewGuid(); var padId = Guid.NewGuid();
        await File.WriteAllTextAsync(path, $$"""
        {
          "Assets": [{ "Id": "{{assetId}}", "Kind": 0, "FilePath": "{{assetId}}.wav", "DisplayName": "Kick", "SchemaVersion": 1, "Duration": null, "ContentHash": null }],
          "Profiles": [{ "Id": "{{profileId}}", "Name": "Legacy profile", "Pads": [{ "Id": "{{padId}}", "Name": "Legacy pad", "SoundAssetId": "{{assetId}}", "Hotkey": "Ctrl+K", "SchemaVersion": 1, "Volume": 0.75, "ColorToken": "Mint", "Order": 4 }], "SchemaVersion": 1 }],
          "ActiveProfileId": "{{profileId}}", "SchemaVersion": 1
        }
        """);
        var store = new JsonFileStore<LibraryState>(path);

        var loaded = (await store.LoadAsync())!;
        var pad = Assert.Single(Assert.Single(loaded.Profiles).Pads);
        Assert.Equal("Audio", pad.IconToken);
        Assert.Equal("Mint", pad.ColorToken);
        Assert.Equal("Legacy profile", loaded.Profiles[0].Name);
        Assert.Equal("Legacy pad", pad.Name);
        Assert.Equal(assetId, pad.SoundAssetId);

        await store.SaveAsync(loaded);
        var reloaded = (await store.LoadAsync())!;
        var savedPad = Assert.Single(Assert.Single(reloaded.Profiles).Pads);
        Assert.Equal("Audio", savedPad.IconToken);
        Assert.Equal("Legacy profile", reloaded.Profiles[0].Name);
        Assert.Equal("Legacy pad", savedPad.Name);
        Assert.Equal(assetId, savedPad.SoundAssetId);
        Assert.Equal(profileId, reloaded.ActiveProfileId);
        Assert.Equal("Kick", Assert.Single(reloaded.Assets).DisplayName);
    }
    [Fact]
    public async Task Library_round_trips_profile_pad_icon_and_custom_color()
    {
        var path = System.IO.Path.Combine(root, "library.json");
        var pad = new SoundPad(Guid.NewGuid(), "Airhorn", null, ColorToken: "#12ABEF", IconToken: "Horn");
        var profile = new Profile(Guid.NewGuid(), "Stream", [pad]);
        var state = new LibraryState([], [profile], profile.Id);
        var store = new JsonFileStore<LibraryState>(path);

        await store.SaveAsync(state);
        var loaded = (await store.LoadAsync())!;
        var loadedProfile = Assert.Single(loaded.Profiles);
        var loadedPad = Assert.Single(loadedProfile.Pads);
        Assert.Equal("Stream", loadedProfile.Name);
        Assert.Equal("Airhorn", loadedPad.Name);
        Assert.Equal("Horn", loadedPad.IconToken);
        Assert.Equal("#12ABEF", loadedPad.ColorToken);
    }
    [Fact]
    public void Portable_marker_wins_over_local_app_data()
    {
        Directory.CreateDirectory(root); File.WriteAllText(System.IO.Path.Combine(root, StoragePaths.PortableMarkerName), "");
        Assert.Equal(System.IO.Path.Combine(root, "data"), StoragePaths.ResolveDataDirectory(root));
    }
    [Fact]
    public async Task Managed_import_uses_guid_and_discards_source_path_link_detects_missing()
    {
        Directory.CreateDirectory(root); var source = System.IO.Path.Combine(root, "source.wav"); await File.WriteAllTextAsync(source, "audio");
        var library = new SoundLibrary(System.IO.Path.Combine(root, "library")); var managed = await library.ImportManagedAsync(source);
        Assert.Equal(SoundAssetKind.Managed, managed.Kind);
        Assert.False(Path.IsPathRooted(managed.FilePath));
        Assert.DoesNotContain("source", managed.FilePath);
        Assert.True(File.Exists(library.ResolvePath(managed)));
        Assert.True(library.IsMissing(library.Link(System.IO.Path.Combine(root, "absent.wav"))));
    }
    [Fact]
    public async Task Managed_delete_stays_inside_private_library_and_never_deletes_linked_original()
    {
        Directory.CreateDirectory(root);
        var source = System.IO.Path.Combine(root, "source.wav");
        await File.WriteAllTextAsync(source, "audio");
        var library = new SoundLibrary(System.IO.Path.Combine(root, "library"));
        var managed = await library.ImportManagedAsync(source);
        var managedPath = library.ResolvePath(managed);

        Assert.True(library.DeleteManaged(managed));
        Assert.False(File.Exists(managedPath));

        var linked = library.Link(source);
        Assert.False(library.DeleteManaged(linked));
        Assert.True(File.Exists(source));
        var escaped = managed with { FilePath = "..\\outside.wav" };
        Assert.False(library.DeleteManaged(escaped));
        Assert.True(File.Exists(source));
    }
    [Fact]
    public async Task Update_check_never_calls_network_without_consent_and_honors_channel()
    {
        var transport = new FakeTransport("[{\"tag_name\":\"v2.0\",\"prerelease\":true,\"html_url\":\"https://example.test/r\"},{\"tag_name\":\"v1.1\",\"prerelease\":false,\"html_url\":\"https://example.test/s\"}]");
        var checker = new GitHubUpdateChecker(transport, new Uri("https://api.github.com/releases"));
        Assert.Null(await checker.CheckAsync(new AppSettings(), new Version(1, 0))); Assert.Equal(0, transport.Calls);
        Assert.Equal("1.1", (await checker.CheckAsync(new AppSettings(true), new Version(1, 0)))!.Version);
        Assert.Equal("2.0", (await checker.CheckAsync(new AppSettings(true, UpdateChannel.Prerelease), new Version(1, 0)))!.Version);
    }
    private sealed class FakeTransport(string result) : IUpdateTransport { public int Calls { get; private set; } public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(result); } }
}
