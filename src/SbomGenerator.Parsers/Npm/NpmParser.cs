using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Parsers.Npm;

/// <summary>
/// Parser for NPM package.json and package-lock.json files.
/// Prefers package-lock.json as the source of truth for resolved dependencies.
/// </summary>
public class NpmParser : IPackageParser
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NpmParser> _logger;

    // Track if we've already parsed a lock file for this repository
    private readonly HashSet<string> _parsedLockFiles = [];

    public PackageEcosystem Ecosystem => PackageEcosystem.Npm;

    public IReadOnlyList<string> SupportedPatterns => ["package.json", "package-lock.json"];

    public NpmParser(IHttpClientFactory httpClientFactory, ILogger<NpmParser> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool CanParse(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName == "package.json" || fileName == "package-lock.json";
    }

    public async Task<IReadOnlyList<Package>> ParseAsync(
        string filePath,
        string fileContent,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var packages = new List<Package>();
        var fileName = Path.GetFileName(filePath);
        var directory = Path.GetDirectoryName(filePath) ?? "";

        try
        {
            using var doc = JsonDocument.Parse(fileContent);
            var root = doc.RootElement;

            if (fileName == "package-lock.json")
            {
                // Parse package-lock.json - this contains the complete resolved dependency tree
                _logger.LogDebug("Parsing lock file: {Path}", filePath);
                packages.AddRange(ParsePackageLock(root, filePath));
                _parsedLockFiles.Add(directory);
            }
            else if (fileName == "package.json")
            {
                // Check if a lock file exists in the same directory
                var lockFilePath = Path.Combine(repositoryRoot, directory, "package-lock.json");
                if (File.Exists(lockFilePath))
                {
                    // Skip package.json if lock file exists - we'll parse the lock file instead
                    _logger.LogDebug("Skipping {Path} - will use package-lock.json instead", filePath);
                    return packages;
                }

                // No lock file - parse package.json (will need registry resolution later)
                _logger.LogDebug("Parsing package.json (no lock file found): {Path}", filePath);
                packages.AddRange(ParsePackageJson(root));
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse {FilePath}", filePath);
        }

        return packages;
    }

    private List<Package> ParsePackageJson(JsonElement root)
    {
        var packages = new List<Package>();

        // Get direct dependencies from package.json
        var directDeps = new HashSet<string>();

        if (root.TryGetProperty("dependencies", out var deps))
        {
            foreach (var dep in deps.EnumerateObject())
            {
                directDeps.Add(dep.Name);
                packages.Add(CreatePackage(dep.Name, dep.Value.GetString() ?? "*", isDirect: true));
            }
        }

        if (root.TryGetProperty("devDependencies", out var devDeps))
        {
            foreach (var dep in devDeps.EnumerateObject())
            {
                directDeps.Add(dep.Name);
                packages.Add(CreatePackage(dep.Name, dep.Value.GetString() ?? "*", isDirect: true));
            }
        }

        return packages;
    }

    private List<Package> ParsePackageLock(JsonElement root, string lockFilePath)
    {
        var packageMap = new Dictionary<string, Package>();

        // First, get the list of direct dependencies from the root package entry
        var directDeps = new HashSet<string>();
        if (root.TryGetProperty("packages", out var pkgsForDirect) &&
            pkgsForDirect.TryGetProperty("", out var rootPkg))
        {
            if (rootPkg.TryGetProperty("dependencies", out var deps))
            {
                foreach (var dep in deps.EnumerateObject())
                {
                    directDeps.Add(dep.Name);
                }
            }
            if (rootPkg.TryGetProperty("devDependencies", out var devDeps))
            {
                foreach (var dep in devDeps.EnumerateObject())
                {
                    directDeps.Add(dep.Name);
                }
            }
        }

        // Handle lockfileVersion 2/3 format (packages object)
        if (root.TryGetProperty("packages", out var pkgs))
        {
            // First pass: Create all packages
            foreach (var pkg in pkgs.EnumerateObject())
            {
                // Skip root package (empty key)
                if (string.IsNullOrEmpty(pkg.Name))
                {
                    continue;
                }

                // Extract package name from node_modules path
                var name = ExtractPackageName(pkg.Name);
                if (string.IsNullOrEmpty(name)) continue;

                var version = pkg.Value.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (string.IsNullOrEmpty(version)) continue;

                // Determine if this is a direct dependency
                var isDirect = directDeps.Contains(name);

                var package = new Package
                {
                    Name = name,
                    Version = version,
                    Ecosystem = PackageEcosystem.Npm,
                    IsDirect = isDirect,
                    Purl = $"pkg:npm/{Uri.EscapeDataString(name)}@{version}"
                };

                // Get resolved URL
                if (pkg.Value.TryGetProperty("resolved", out var resolved))
                {
                    package.DownloadUrl = resolved.GetString();
                }

                // Get integrity hash
                if (pkg.Value.TryGetProperty("integrity", out var integrity))
                {
                    package.Sha256 = integrity.GetString();
                }

                // Get license if present
                if (pkg.Value.TryGetProperty("license", out var license))
                {
                    package.License = license.GetString();
                }

                // Use name@version as key to handle multiple versions
                var key = $"{name}@{version}";
                packageMap[key] = package;
            }

            // Second pass: Build dependency relationships
            foreach (var pkg in pkgs.EnumerateObject())
            {
                if (string.IsNullOrEmpty(pkg.Name)) continue;

                var name = ExtractPackageName(pkg.Name);
                var version = pkg.Value.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version)) continue;

                var key = $"{name}@{version}";
                if (!packageMap.TryGetValue(key, out var package)) continue;

                // Parse this package's dependencies
                if (pkg.Value.TryGetProperty("dependencies", out var deps))
                {
                    foreach (var dep in deps.EnumerateObject())
                    {
                        var depName = dep.Name;
                        var depVersionRange = dep.Value.GetString() ?? "*";

                        // Find the resolved version in our package map
                        var resolvedPackage = FindResolvedPackage(packageMap, depName, depVersionRange);

                        package.Dependencies.Add(new PackageDependency
                        {
                            Name = depName,
                            VersionRange = depVersionRange,
                            ResolvedVersion = resolvedPackage?.Version
                        });
                    }
                }
            }
        }
        // Handle lockfileVersion 1 format (dependencies object)
        else if (root.TryGetProperty("dependencies", out var deps))
        {
            ParseLockV1Dependencies(deps, packageMap, directDeps, null);
        }

        var packages = packageMap.Values.ToList();
        _logger.LogInformation("Parsed {Count} packages from {Path}", packages.Count, lockFilePath);
        return packages;
    }

    /// <summary>
    /// Finds a resolved package by name, preferring exact version match.
    /// </summary>
    private static Package? FindResolvedPackage(Dictionary<string, Package> packageMap, string name, string versionRange)
    {
        // First try exact match (for pinned versions in lock file)
        var cleanVersion = versionRange.TrimStart('^', '~', '>', '<', '=', ' ');
        var exactKey = $"{name}@{cleanVersion}";
        if (packageMap.TryGetValue(exactKey, out var exactMatch))
        {
            return exactMatch;
        }

        // Otherwise find any version of this package
        return packageMap.Values.FirstOrDefault(p => p.Name == name);
    }

    private static string ExtractPackageName(string nodePath)
    {
        // Path format: "node_modules/@scope/package" or "node_modules/package"
        // Can also be nested: "node_modules/pkg1/node_modules/pkg2"

        if (!nodePath.StartsWith("node_modules/"))
        {
            return nodePath;
        }

        // Get the last package in the path (handles nested node_modules)
        var lastNodeModules = nodePath.LastIndexOf("node_modules/");
        var name = nodePath[(lastNodeModules + "node_modules/".Length)..];

        return name;
    }

    private void ParseLockV1Dependencies(
        JsonElement deps, 
        Dictionary<string, Package> packageMap, 
        HashSet<string> directDeps,
        Package? parentPackage)
    {
        foreach (var dep in deps.EnumerateObject())
        {
            var name = dep.Name;
            var version = dep.Value.TryGetProperty("version", out var v) ? v.GetString() : null;

            if (!string.IsNullOrEmpty(version))
            {
                var isDirect = directDeps.Contains(name);
                var key = $"{name}@{version}";

                Package package;
                if (!packageMap.TryGetValue(key, out package!))
                {
                    package = new Package
                    {
                        Name = name,
                        Version = version,
                        Ecosystem = PackageEcosystem.Npm,
                        IsDirect = isDirect,
                        Purl = $"pkg:npm/{Uri.EscapeDataString(name)}@{version}"
                    };

                    if (dep.Value.TryGetProperty("resolved", out var resolved))
                    {
                        package.DownloadUrl = resolved.GetString();
                    }

                    if (dep.Value.TryGetProperty("integrity", out var integrity))
                    {
                        package.Sha256 = integrity.GetString();
                    }

                    packageMap[key] = package;
                }

                // Add as dependency of parent package
                if (parentPackage != null)
                {
                    parentPackage.Dependencies.Add(new PackageDependency
                    {
                        Name = name,
                        VersionRange = version,
                        ResolvedVersion = version
                    });
                }

                // Parse this package's requires (dependencies)
                if (dep.Value.TryGetProperty("requires", out var requires))
                {
                    foreach (var req in requires.EnumerateObject())
                    {
                        var reqName = req.Name;
                        var reqVersion = req.Value.GetString() ?? "*";
                        
                        // Find the resolved package
                        var resolvedPkg = FindResolvedPackage(packageMap, reqName, reqVersion);
                        
                        package.Dependencies.Add(new PackageDependency
                        {
                            Name = reqName,
                            VersionRange = reqVersion,
                            ResolvedVersion = resolvedPkg?.Version
                        });
                    }
                }

                // Recurse into nested dependencies
                if (dep.Value.TryGetProperty("dependencies", out var nested))
                {
                    ParseLockV1Dependencies(nested, packageMap, directDeps, package);
                }
            }
        }
    }

    private Package CreatePackage(string name, string version, bool isDirect)
    {
        // Clean version string (remove ^ ~ >= etc.)
        var cleanVersion = version.TrimStart('^', '~', '>', '<', '=', ' ');
        if (cleanVersion.Contains(' '))
        {
            cleanVersion = cleanVersion.Split(' ')[0];
        }

        return new Package
        {
            Name = name,
            Version = cleanVersion,
            Ecosystem = PackageEcosystem.Npm,
            IsDirect = isDirect,
            Purl = $"pkg:npm/{Uri.EscapeDataString(name)}@{cleanVersion}"
        };
    }

    public async Task<IReadOnlyList<Package>> ResolveTransitiveDependenciesAsync(
        IReadOnlyList<Package> packages,
        CancellationToken cancellationToken = default)
    {
        // Check if all packages came from a lock file (they'll have resolved URLs)
        var allFromLockFile = packages.All(p => !string.IsNullOrEmpty(p.DownloadUrl) || !string.IsNullOrEmpty(p.Sha256));

        if (allFromLockFile)
        {
            // Lock file already contains complete dependency tree - just enrich with metadata
            _logger.LogDebug("Packages from lock file - skipping registry resolution, enriching metadata only");
            return await EnrichPackageMetadataAsync(packages, cancellationToken);
        }

        // No lock file - need to resolve from registry (fallback behavior)
        _logger.LogDebug("No lock file - resolving dependencies from npm registry");
        return await ResolveFromRegistryAsync(packages, cancellationToken);
    }

    private async Task<IReadOnlyList<Package>> EnrichPackageMetadataAsync(
        IReadOnlyList<Package> packages,
        CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri("https://registry.npmjs.org/");

        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip if we already have license info
            if (!string.IsNullOrEmpty(package.License))
            {
                continue;
            }

            try
            {
                var encodedName = Uri.EscapeDataString(package.Name);
                var response = await httpClient.GetAsync($"{encodedName}/{package.Version}", cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

                // Get license
                if (json.TryGetProperty("license", out var license))
                {
                    package.License = license.ValueKind == JsonValueKind.String
                        ? license.GetString()
                        : license.TryGetProperty("type", out var licenseType)
                            ? licenseType.GetString()
                            : null;
                }

                // Get description
                if (json.TryGetProperty("description", out var desc))
                {
                    package.Description = desc.GetString();
                }

                // Get homepage
                if (json.TryGetProperty("homepage", out var homepage))
                {
                    package.Homepage = homepage.GetString();
                }

                // Get download URL if not already set
                if (string.IsNullOrEmpty(package.DownloadUrl) && json.TryGetProperty("dist", out var dist))
                {
                    if (dist.TryGetProperty("tarball", out var tarball))
                    {
                        package.DownloadUrl = tarball.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to enrich metadata for {Package}", package.Name);
            }
        }

        return packages.ToList();
    }

    private async Task<IReadOnlyList<Package>> ResolveFromRegistryAsync(
        IReadOnlyList<Package> packages,
        CancellationToken cancellationToken)
    {
        var allPackages = new Dictionary<string, Package>();
        var toResolve = new Queue<Package>(packages);

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri("https://registry.npmjs.org/");

        while (toResolve.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var package = toResolve.Dequeue();
            var key = $"{package.Name}@{package.Version}";

            if (allPackages.ContainsKey(key))
            {
                continue;
            }

            allPackages[key] = package;

            try
            {
                var encodedName = Uri.EscapeDataString(package.Name);
                var response = await httpClient.GetAsync($"{encodedName}/{package.Version}", cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to fetch {Package}: {Status}", key, response.StatusCode);
                    continue;
                }

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

                // Get license
                if (json.TryGetProperty("license", out var license))
                {
                    package.License = license.ValueKind == JsonValueKind.String
                        ? license.GetString()
                        : license.TryGetProperty("type", out var licenseType)
                            ? licenseType.GetString()
                            : null;
                }

                // Get description
                if (json.TryGetProperty("description", out var desc))
                {
                    package.Description = desc.GetString();
                }

                // Get homepage
                if (json.TryGetProperty("homepage", out var homepage))
                {
                    package.Homepage = homepage.GetString();
                }

                // Get dist info
                if (json.TryGetProperty("dist", out var dist))
                {
                    if (dist.TryGetProperty("tarball", out var tarball))
                    {
                        package.DownloadUrl = tarball.GetString();
                    }
                    if (dist.TryGetProperty("shasum", out var shasum))
                    {
                        package.Sha256 = shasum.GetString();
                    }
                }

                // Queue dependencies for resolution
                if (json.TryGetProperty("dependencies", out var deps))
                {
                    foreach (var dep in deps.EnumerateObject())
                    {
                        var depVersion = dep.Value.GetString() ?? "*";
                        var cleanVersion = depVersion.TrimStart('^', '~', '>', '<', '=', ' ');
                        if (cleanVersion.Contains(' '))
                        {
                            cleanVersion = cleanVersion.Split(' ')[0];
                        }

                        var depKey = $"{dep.Name}@{cleanVersion}";
                        if (!allPackages.ContainsKey(depKey))
                        {
                            toResolve.Enqueue(new Package
                            {
                                Name = dep.Name,
                                Version = cleanVersion,
                                Ecosystem = PackageEcosystem.Npm,
                                IsDirect = false,
                                Purl = $"pkg:npm/{Uri.EscapeDataString(dep.Name)}@{cleanVersion}"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resolving {Package}", key);
            }
        }

        return allPackages.Values.ToList();
    }
}
