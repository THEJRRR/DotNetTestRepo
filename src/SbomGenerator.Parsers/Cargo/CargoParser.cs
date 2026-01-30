using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Parsers.Cargo;

/// <summary>
/// Parser for Rust Cargo.toml and Cargo.lock files.
/// </summary>
public partial class CargoParser : IPackageParser
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CargoParser> _logger;

    public PackageEcosystem Ecosystem => PackageEcosystem.Cargo;

    public IReadOnlyList<string> SupportedPatterns => ["Cargo.toml", "Cargo.lock"];

    public CargoParser(IHttpClientFactory httpClientFactory, ILogger<CargoParser> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool CanParse(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName is "Cargo.toml" or "Cargo.lock";
    }

    public async Task<IReadOnlyList<Package>> ParseAsync(
        string filePath,
        string fileContent,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(filePath);

        return fileName switch
        {
            "Cargo.lock" => ParseCargoLock(fileContent),
            "Cargo.toml" => ParseCargoToml(fileContent),
            _ => []
        };
    }

    private List<Package> ParseCargoLock(string content)
    {
        var packageMap = new Dictionary<string, Package>();
        var packageDeps = new Dictionary<string, List<string>>(); // package key -> list of dep strings
        var lines = content.Split('\n');

        string? currentName = null;
        string? currentVersion = null;
        string? currentChecksum = null;
        List<string>? currentDeps = null;
        var inDependencies = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line == "[[package]]")
            {
                // Save previous package
                if (currentName != null && currentVersion != null)
                {
                    var pkg = CreatePackage(currentName, currentVersion, currentChecksum);
                    var key = $"{currentName}@{currentVersion}";
                    packageMap[key] = pkg;
                    if (currentDeps != null && currentDeps.Count > 0)
                    {
                        packageDeps[key] = currentDeps;
                    }
                }
                currentName = null;
                currentVersion = null;
                currentChecksum = null;
                currentDeps = null;
                inDependencies = false;
                continue;
            }

            if (line.StartsWith("name = "))
            {
                currentName = line["name = ".Length..].Trim('"');
                inDependencies = false;
            }
            else if (line.StartsWith("version = "))
            {
                currentVersion = line["version = ".Length..].Trim('"');
                inDependencies = false;
            }
            else if (line.StartsWith("checksum = "))
            {
                currentChecksum = line["checksum = ".Length..].Trim('"');
                inDependencies = false;
            }
            else if (line == "dependencies = [")
            {
                inDependencies = true;
                currentDeps = [];
            }
            else if (line == "]")
            {
                inDependencies = false;
            }
            else if (inDependencies && line.StartsWith('"'))
            {
                // Parse dependency like "package_name version" or "package_name"
                var depStr = line.Trim('"', ',', ' ');
                currentDeps?.Add(depStr);
            }
        }

        // Don't forget the last package
        if (currentName != null && currentVersion != null)
        {
            var pkg = CreatePackage(currentName, currentVersion, currentChecksum);
            var key = $"{currentName}@{currentVersion}";
            packageMap[key] = pkg;
            if (currentDeps != null && currentDeps.Count > 0)
            {
                packageDeps[key] = currentDeps;
            }
        }

        // Second pass: Link dependencies
        foreach (var (pkgKey, deps) in packageDeps)
        {
            if (!packageMap.TryGetValue(pkgKey, out var package)) continue;

            foreach (var depStr in deps)
            {
                // Format: "name version" or just "name"
                var parts = depStr.Split(' ', 2);
                var depName = parts[0];
                var depVersion = parts.Length > 1 ? parts[1] : null;

                // Find the resolved package
                Package? resolvedPkg = null;
                if (depVersion != null)
                {
                    packageMap.TryGetValue($"{depName}@{depVersion}", out resolvedPkg);
                }
                else
                {
                    resolvedPkg = packageMap.Values.FirstOrDefault(p => p.Name == depName);
                }

                package.Dependencies.Add(new PackageDependency
                {
                    Name = depName,
                    VersionRange = depVersion,
                    ResolvedVersion = resolvedPkg?.Version
                });
            }
        }

        return packageMap.Values.ToList();
    }

    private List<Package> ParseCargoToml(string content)
    {
        var packages = new List<Package>();
        var inDependencies = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("[dependencies]") ||
                line.StartsWith("[dev-dependencies]") ||
                line.StartsWith("[build-dependencies]"))
            {
                inDependencies = true;
                continue;
            }

            if (line.StartsWith('[') && inDependencies)
            {
                inDependencies = false;
                continue;
            }

            if (inDependencies && line.Contains('=') && !line.StartsWith('#'))
            {
                var match = TomlDependencyRegex().Match(line);
                if (match.Success)
                {
                    var name = match.Groups["name"].Value;
                    var version = match.Groups["version"].Value;

                    // Handle table-style dependencies: package = { version = "1.0" }
                    if (string.IsNullOrEmpty(version))
                    {
                        var tableMatch = TomlTableVersionRegex().Match(line);
                        if (tableMatch.Success)
                        {
                            version = tableMatch.Groups["version"].Value;
                        }
                    }

                    if (!string.IsNullOrEmpty(version))
                    {
                        packages.Add(CreatePackage(name, version.TrimStart('^', '~', '=', ' '), null));
                    }
                }
            }
        }

        return packages;
    }

    private Package CreatePackage(string name, string version, string? checksum)
    {
        return new Package
        {
            Name = name,
            Version = version,
            Ecosystem = PackageEcosystem.Cargo,
            IsDirect = true,
            Sha256 = checksum,
            Purl = $"pkg:cargo/{name}@{version}",
            DownloadUrl = $"https://crates.io/api/v1/crates/{name}/{version}/download"
        };
    }

    public async Task<IReadOnlyList<Package>> ResolveTransitiveDependenciesAsync(
        IReadOnlyList<Package> packages,
        CancellationToken cancellationToken = default)
    {
        var allPackages = new Dictionary<string, Package>();

        using var httpClient = _httpClientFactory.CreateClient();

        foreach (var package in packages)
        {
            var key = $"{package.Name}@{package.Version}";
            if (allPackages.ContainsKey(key)) continue;

            allPackages[key] = package;

            try
            {
                var response = await httpClient.GetAsync(
                    $"https://crates.io/api/v1/crates/{package.Name}/{package.Version}",
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);

                    if (json.TryGetProperty("version", out var versionInfo))
                    {
                        if (versionInfo.TryGetProperty("license", out var license))
                            package.License = license.GetString();

                        if (versionInfo.TryGetProperty("description", out var desc))
                            package.Description = desc.GetString();
                    }

                    if (json.TryGetProperty("crate", out var crateInfo))
                    {
                        if (crateInfo.TryGetProperty("homepage", out var homepage))
                            package.Homepage = homepage.GetString();

                        if (crateInfo.TryGetProperty("repository", out var repo))
                            package.Homepage ??= repo.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resolving {Package}", package.Name);
            }
        }

        return allPackages.Values.ToList();
    }

    [GeneratedRegex(@"^(?<name>[a-zA-Z0-9_\-]+)\s*=\s*""(?<version>[^""]+)""")]
    private static partial Regex TomlDependencyRegex();

    [GeneratedRegex(@"version\s*=\s*""(?<version>[^""]+)""")]
    private static partial Regex TomlTableVersionRegex();
}
