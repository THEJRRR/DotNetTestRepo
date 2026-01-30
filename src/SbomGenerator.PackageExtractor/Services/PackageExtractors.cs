using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.PackageExtractor.Services;

/// <summary>
/// Base class for package file extractors with common functionality.
/// </summary>
public abstract class BasePackageExtractor
{
    protected readonly IHttpClientFactory HttpClientFactory;
    protected readonly ILogger Logger;

    protected BasePackageExtractor(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        HttpClientFactory = httpClientFactory;
        Logger = logger;
    }

    protected async Task<IReadOnlyList<PackageFile>> ExtractFromArchiveAsync(
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        var files = new List<PackageFile>();

        try
        {
            using var httpClient = HttpClientFactory.CreateClient();
            using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning("Failed to download package from {Url}: {Status}", downloadUrl, response.StatusCode);
                return files;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;

            using var archive = ArchiveFactory.Open(memoryStream);

            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = new PackageFile
                {
                    Path = entry.Key ?? "unknown",
                    Size = entry.Size
                };

                // Calculate SHA256 hash
                try
                {
                    using var entryStream = entry.OpenEntryStream();
                    using var sha256 = SHA256.Create();
                    var hash = await sha256.ComputeHashAsync(entryStream, cancellationToken);
                    file.Sha256 = Convert.ToHexString(hash).ToLowerInvariant();
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to compute hash for {Path}", entry.Key);
                }

                files.Add(file);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to extract files from {Url}", downloadUrl);
        }

        return files;
    }
}

/// <summary>
/// Extracts file listings from NPM packages (tarballs).
/// </summary>
public class NpmPackageExtractor : BasePackageExtractor, IPackageFileExtractor
{
    public PackageEcosystem Ecosystem => PackageEcosystem.Npm;

    public NpmPackageExtractor(IHttpClientFactory httpClientFactory, ILogger<NpmPackageExtractor> logger)
        : base(httpClientFactory, logger) { }

    public async Task<IReadOnlyList<PackageFile>> ExtractFilesAsync(
        Package package,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(package.DownloadUrl))
        {
            // Construct download URL if not present
            var encodedName = Uri.EscapeDataString(package.Name);
            package.DownloadUrl = $"https://registry.npmjs.org/{encodedName}/-/{package.Name.Split('/').Last()}-{package.Version}.tgz";
        }

        return await ExtractFromArchiveAsync(package.DownloadUrl, cancellationToken);
    }
}

/// <summary>
/// Extracts file listings from NuGet packages (.nupkg files).
/// </summary>
public class NuGetPackageExtractor : BasePackageExtractor, IPackageFileExtractor
{
    public PackageEcosystem Ecosystem => PackageEcosystem.NuGet;

    public NuGetPackageExtractor(IHttpClientFactory httpClientFactory, ILogger<NuGetPackageExtractor> logger)
        : base(httpClientFactory, logger) { }

    public async Task<IReadOnlyList<PackageFile>> ExtractFilesAsync(
        Package package,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(package.DownloadUrl))
        {
            var id = package.Name.ToLowerInvariant();
            var version = package.Version.ToLowerInvariant();
            package.DownloadUrl = $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg";
        }

        return await ExtractFromArchiveAsync(package.DownloadUrl, cancellationToken);
    }
}

/// <summary>
/// Extracts file listings from PyPI packages (wheels or tarballs).
/// </summary>
public class PyPIPackageExtractor : BasePackageExtractor, IPackageFileExtractor
{
    public PackageEcosystem Ecosystem => PackageEcosystem.PyPI;

    public PyPIPackageExtractor(IHttpClientFactory httpClientFactory, ILogger<PyPIPackageExtractor> logger)
        : base(httpClientFactory, logger) { }

    public async Task<IReadOnlyList<PackageFile>> ExtractFilesAsync(
        Package package,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(package.DownloadUrl))
        {
            // Try to get download URL from PyPI API
            using var httpClient = HttpClientFactory.CreateClient();
            try
            {
                var response = await httpClient.GetAsync(
                    $"https://pypi.org/pypi/{package.Name}/{package.Version}/json",
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);
                    if (json.TryGetProperty("urls", out var urls))
                    {
                        foreach (var url in urls.EnumerateArray())
                        {
                            if (url.TryGetProperty("url", out var downloadUrl))
                            {
                                package.DownloadUrl = downloadUrl.GetString();
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to get download URL for {Package}", package.Name);
            }
        }

        if (string.IsNullOrEmpty(package.DownloadUrl))
        {
            return [];
        }

        return await ExtractFromArchiveAsync(package.DownloadUrl, cancellationToken);
    }
}

/// <summary>
/// Extracts file listings from Maven packages (JAR files).
/// </summary>
public class MavenPackageExtractor : BasePackageExtractor, IPackageFileExtractor
{
    public PackageEcosystem Ecosystem => PackageEcosystem.Maven;

    public MavenPackageExtractor(IHttpClientFactory httpClientFactory, ILogger<MavenPackageExtractor> logger)
        : base(httpClientFactory, logger) { }

    public async Task<IReadOnlyList<PackageFile>> ExtractFilesAsync(
        Package package,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(package.DownloadUrl))
        {
            var parts = package.Name.Split(':');
            if (parts.Length == 2)
            {
                var groupPath = parts[0].Replace('.', '/');
                var artifactId = parts[1];
                package.DownloadUrl = $"https://repo1.maven.org/maven2/{groupPath}/{artifactId}/{package.Version}/{artifactId}-{package.Version}.jar";
            }
        }

        if (string.IsNullOrEmpty(package.DownloadUrl))
        {
            return [];
        }

        return await ExtractFromArchiveAsync(package.DownloadUrl, cancellationToken);
    }
}

/// <summary>
/// Extracts file listings from Cargo crates.
/// </summary>
public class CargoPackageExtractor : BasePackageExtractor, IPackageFileExtractor
{
    public PackageEcosystem Ecosystem => PackageEcosystem.Cargo;

    public CargoPackageExtractor(IHttpClientFactory httpClientFactory, ILogger<CargoPackageExtractor> logger)
        : base(httpClientFactory, logger) { }

    public async Task<IReadOnlyList<PackageFile>> ExtractFilesAsync(
        Package package,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(package.DownloadUrl))
        {
            package.DownloadUrl = $"https://crates.io/api/v1/crates/{package.Name}/{package.Version}/download";
        }

        return await ExtractFromArchiveAsync(package.DownloadUrl, cancellationToken);
    }
}

/// <summary>
/// Extracts file listings from Go modules.
/// </summary>
public class GoPackageExtractor : BasePackageExtractor, IPackageFileExtractor
{
    public PackageEcosystem Ecosystem => PackageEcosystem.Go;

    public GoPackageExtractor(IHttpClientFactory httpClientFactory, ILogger<GoPackageExtractor> logger)
        : base(httpClientFactory, logger) { }

    public async Task<IReadOnlyList<PackageFile>> ExtractFilesAsync(
        Package package,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(package.DownloadUrl))
        {
            var encodedModule = Uri.EscapeDataString(package.Name);
            package.DownloadUrl = $"https://proxy.golang.org/{encodedModule}/@v/{package.Version}.zip";
        }

        return await ExtractFromArchiveAsync(package.DownloadUrl, cancellationToken);
    }
}

/// <summary>
/// Extracts file listings from RubyGems.
/// </summary>
public class RubyGemsPackageExtractor : BasePackageExtractor, IPackageFileExtractor
{
    public PackageEcosystem Ecosystem => PackageEcosystem.RubyGems;

    public RubyGemsPackageExtractor(IHttpClientFactory httpClientFactory, ILogger<RubyGemsPackageExtractor> logger)
        : base(httpClientFactory, logger) { }

    public async Task<IReadOnlyList<PackageFile>> ExtractFilesAsync(
        Package package,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(package.DownloadUrl))
        {
            package.DownloadUrl = $"https://rubygems.org/downloads/{package.Name}-{package.Version}.gem";
        }

        // RubyGems are tar archives containing data.tar.gz
        // For simplicity, we'll just list the outer archive contents
        return await ExtractFromArchiveAsync(package.DownloadUrl, cancellationToken);
    }
}
