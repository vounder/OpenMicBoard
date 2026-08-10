using System.Text.Json;
using OpenMicBoard.Core;

namespace OpenMicBoard.Infrastructure;

/// <summary>Resolves app data beside a portable marker, otherwise under LocalAppData.</summary>
public static class StoragePaths
{
    public const string PortableMarkerName = "OpenMicBoard.portable";

    public static string ResolveDataDirectory(string applicationDirectory, string applicationName = "OpenMicBoard")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        return File.Exists(Path.Combine(applicationDirectory, PortableMarkerName))
            ? Path.Combine(applicationDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), applicationName);
    }
}

/// <summary>Single-process JSON storage with durable temp writes, validation, and backup recovery.</summary>
public sealed class JsonFileStore<T> where T : class, ISchemaVersioned
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly JsonSerializerOptions options = new()
    {
        WriteIndented = true
    };

    public JsonFileStore(string path, int supportedSchemaVersion = 1)
    {
        Path = path;
        SupportedSchemaVersion = supportedSchemaVersion;
    }

    public string Path { get; }

    public int SupportedSchemaVersion { get; }

    public async Task SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        Validate(value);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            var temp = Path + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            _ = await ReadValidatedAsync(temp, cancellationToken).ConfigureAwait(false);
            var backup = Path + ".bak";
            if (File.Exists(Path))
            {
                File.Replace(temp, Path, backup, true);
            }
            else
            {
                File.Move(temp, Path);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<T?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            return await ReadValidatedAsync(Path, cancellationToken).ConfigureAwait(false);
        }
        catch when (File.Exists(Path + ".bak"))
        {
            return await ReadValidatedAsync(Path + ".bak", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<T> ReadValidatedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("JSON did not contain a record.");
        Validate(value);
        return value;
    }

    private void Validate(T value)
    {
        if (value.SchemaVersion > SupportedSchemaVersion)
        {
            throw new NotSupportedException($"Schema {value.SchemaVersion} is newer than supported schema {SupportedSchemaVersion}.");
        }
    }
}

/// <summary>Imports managed files into the private library using an opaque GUID name; source paths are never persisted.</summary>
public sealed class SoundLibrary(string libraryDirectory)
{
    public async Task<SoundAsset> ImportManagedAsync(string sourcePath, string? displayName = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source sound was not found.", sourcePath);
        }

        Directory.CreateDirectory(libraryDirectory);
        var id = Guid.NewGuid();
        var managedFileName = id + System.IO.Path.GetExtension(sourcePath);
        var destination = System.IO.Path.Combine(libraryDirectory, managedFileName);
        await using var input = File.OpenRead(sourcePath);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return new SoundAsset(
            id,
            SoundAssetKind.Managed,
            managedFileName,
            displayName ?? System.IO.Path.GetFileName(sourcePath));
    }

    public SoundAsset Link(string path, string? displayName = null) =>
        new(
            Guid.NewGuid(),
            SoundAssetKind.Linked,
            path,
            displayName ?? System.IO.Path.GetFileName(path));

    public string ResolvePath(SoundAsset asset) =>
        asset.Kind == SoundAssetKind.Managed
            ? System.IO.Path.Combine(libraryDirectory, System.IO.Path.GetFileName(asset.FilePath))
            : asset.FilePath;

    public bool IsMissing(SoundAsset asset) => !File.Exists(ResolvePath(asset));

    public bool DeleteManaged(SoundAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Kind != SoundAssetKind.Managed)
        {
            return false;
        }

        var libraryRoot = System.IO.Path.GetFullPath(libraryDirectory)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar) +
            System.IO.Path.DirectorySeparatorChar;
        var managedPath = System.IO.Path.GetFullPath(ResolvePath(asset));
        if (!managedPath.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Managed sound path escaped the private library.");
        }

        if (!File.Exists(managedPath))
        {
            return false;
        }

        File.Delete(managedPath);
        return true;
    }
}

/// <summary>
/// HTTP transport restricted to the public GitHub API. It is only invoked by
/// <see cref="GitHubUpdateChecker"/> after explicit consent.
/// </summary>
public sealed class GitHubHttpTransport : IUpdateTransport, IDisposable
{
    private readonly HttpClient client;

    public GitHubHttpTransport(HttpMessageHandler? handler = null)
    {
        client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenMicBoard/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<string> GetStringAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Update requests are restricted to api.github.com.");
        }

        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => client.Dispose();
}
