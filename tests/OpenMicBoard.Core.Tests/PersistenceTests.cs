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
    public async Task Corrupt_primary_recovers_backup()
    {
        var path = System.IO.Path.Combine(root, "settings.json"); var store = new JsonFileStore<AppSettings>(path);
        await store.SaveAsync(new AppSettings(true)); await store.SaveAsync(new AppSettings(false));
        await File.WriteAllTextAsync(path, "{");
        Assert.True((await store.LoadAsync())!.CheckForUpdates);
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
