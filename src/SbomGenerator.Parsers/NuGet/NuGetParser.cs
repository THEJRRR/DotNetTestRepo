using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Parsers.NuGet;

/// <summary>
/// Parser for NuGet project files and lock files.
/// Prefers packages.lock.json as the source of truth when available.
/// </summary>
public partial class NuGetParser : IPackageParser
{
    private readonly ILogger<NuGetParser> _logger;
    private readonly HashSet<string> _parsedLockFiles = [];

    public PackageEcosystem Ecosystem => PackageEcosystem.NuGet;

    public IReadOnlyList<string> SupportedPatterns =>
        ["packages.lock.json", "*.csproj", "*.fsproj", "*.vbproj", "packages.config", "Directory.Packages.props"];

    public NuGetParser(ILogger<NuGetParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return fileName == "packages.lock.json" ||
               ext is ".csproj" or ".fsproj" or ".vbproj" ||
               fileName is "packages.config" or "Directory.Packages.props";
    }

    public async Task<IReadOnlyList<Package>> ParseAsync(
        string filePath,
        string fileContent,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var packages = new List<Package>();
        var fileName = Path.GetFileName(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var directory = Path.GetDirectoryName(filePath) ?? "";

        try
        {
            if (fileName == "packages.lock.json")
            {
                // Parse lock file - this is the source of truth
                _logger.LogDebug("Parsing NuGet lock file: {Path}", filePath);
                packages.AddRange(ParsePackagesLockJson(fileContent, filePath));
                _parsedLockFiles.Add(directory);
            }
            else if (ext is ".csproj" or ".fsproj" or ".vbproj")
            {
                // Check if lock file exists in same directory - skip if so
                var lockFilePath = Path.Combine(repositoryRoot, directory, "packages.lock.json");
                if (File.Exists(lockFilePath))
                {
                    _logger.LogDebug("Skipping {Path} - will use packages.lock.json instead", filePath);
                    return packages;
                }

                packages.AddRange(ParseProjectFile(fileContent, repositoryRoot, filePath));
            }
            else if (fileName == "packages.config")
            {
                packages.AddRange(ParsePackagesConfig(fileContent));
            }
            else if (fileName == "Directory.Packages.props")
            {
                packages.AddRange(ParseDirectoryPackagesProps(fileContent));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse {FilePath}", filePath);
        }

        return packages;
    }

    private List<Package> ParsePackagesLockJson(string content, string lockFilePath)
    {
        var packageMap = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (!root.TryGetProperty("dependencies", out var dependencies))
            {
                _logger.LogWarning("No dependencies found in {Path}", lockFilePath);
                return [];
            }

            // Iterate through target frameworks (e.g., "net8.0")
            foreach (var tfm in dependencies.EnumerateObject())
            {
                var targetFramework = tfm.Name;
                _logger.LogDebug("Parsing dependencies for {TFM}", targetFramework);

                // First pass: Create all packages
                foreach (var pkg in tfm.Value.EnumerateObject())
                {
                    var name = pkg.Name;
                    
                    var resolved = pkg.Value.TryGetProperty("resolved", out var resolvedProp) 
                        ? resolvedProp.GetString() 
                        : null;

                    if (string.IsNullOrEmpty(resolved))
                    {
                        continue;
                    }

                    var key = $"{name}@{resolved}";
                    if (packageMap.ContainsKey(key))
                    {
                        continue; // Already added from another TFM
                    }

                    // Determine if direct or transitive
                    var type = pkg.Value.TryGetProperty("type", out var typeProp) 
                        ? typeProp.GetString() 
                        : "Transitive";
                    var isDirect = type?.Equals("Direct", StringComparison.OrdinalIgnoreCase) == true;

                    // Get content hash for integrity
                    var contentHash = pkg.Value.TryGetProperty("contentHash", out var hashProp) 
                        ? hashProp.GetString() 
                        : null;

                    var package = new Package
                    {
                        Name = name,
                        Version = resolved,
                        Ecosystem = PackageEcosystem.NuGet,
                        IsDirect = isDirect,
                        Purl = $"pkg:nuget/{name}@{resolved}",
                        Sha256 = contentHash,
                        DownloadUrl = $"https://api.nuget.org/v3-flatcontainer/{name.ToLowerInvariant()}/{resolved.ToLowerInvariant()}/{name.ToLowerInvariant()}.{resolved.ToLowerInvariant()}.nupkg"
                    };

                    packageMap[key] = package;
                }

                // Second pass: Build dependency relationships
                foreach (var pkg in tfm.Value.EnumerateObject())
                {
                    var name = pkg.Name;
                    var resolved = pkg.Value.TryGetProperty("resolved", out var resolvedProp) 
                        ? resolvedProp.GetString() 
                        : null;

                    if (string.IsNullOrEmpty(resolved))
                    {
                        continue;
                    }

                    var key = $"{name}@{resolved}";
                    if (!packageMap.TryGetValue(key, out var package))
                    {
                        continue;
                    }

                    // Parse this package's dependencies
                    if (pkg.Value.TryGetProperty("dependencies", out var deps))
                    {
                        foreach (var dep in deps.EnumerateObject())
                        {
                            var depName = dep.Name;
                            var depVersion = dep.Value.GetString();

                            // Find the resolved package
                            var depKey = $"{depName}@{depVersion}";
                            packageMap.TryGetValue(depKey, out var resolvedDep);

                            package.Dependencies.Add(new PackageDependency
                            {
                                Name = depName,
                                VersionRange = depVersion,
                                ResolvedVersion = resolvedDep?.Version ?? depVersion
                            });
                        }
                    }
                }

                // Only parse first TFM to avoid duplicates
                break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse packages.lock.json: {Path}", lockFilePath);
        }

        _logger.LogInformation("Parsed {Count} packages from {Path}", packageMap.Count, lockFilePath);
        return packageMap.Values.ToList();
    }

    private IEnumerable<Package> ParsePackagesConfig(string content)
    {
        var doc = XDocument.Parse(content);
        var packages = doc.Descendants("package");

        foreach (var pkg in packages)
        {
            var id = pkg.Attribute("id")?.Value;
            var version = pkg.Attribute("version")?.Value;

            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(version))
            {
                yield return new Package
                {
                    Name = id,
                    Version = version,
                    Ecosystem = PackageEcosystem.NuGet,
                    IsDirect = true,
                    Purl = $"pkg:nuget/{id}@{version}"
                };
            }
        }
    }

    private IEnumerable<Package> ParseProjectFile(string content, string repositoryRoot, string filePath)
    {
        var doc = XDocument.Parse(content);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        // Check if using Central Package Management
        var useCpm = doc.Descendants()
            .Any(e => e.Name.LocalName == "ManagePackageVersionsCentrally" &&
                     e.Value.Equals("true", StringComparison.OrdinalIgnoreCase));

        // Get version overrides from Directory.Packages.props if using CPM
        var versionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (useCpm)
        {
            versionMap = LoadCentralVersions(repositoryRoot, filePath);
        }

        // Find PackageReference elements
        var packageRefs = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference");

        foreach (var pkgRef in packageRefs)
        {
            var include = pkgRef.Attribute("Include")?.Value;
            if (string.IsNullOrEmpty(include)) continue;

            // Get version from attribute or child element
            var version = pkgRef.Attribute("Version")?.Value ??
                         pkgRef.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;

            // If no version and using CPM, look up in central versions
            if (string.IsNullOrEmpty(version) && versionMap.TryGetValue(include, out var centralVersion))
            {
                version = centralVersion;
            }

            if (string.IsNullOrEmpty(version))
            {
                _logger.LogWarning("No version found for package {Package}", include);
                continue;
            }

            yield return new Package
            {
                Name = include,
                Version = version,
                Ecosystem = PackageEcosystem.NuGet,
                IsDirect = true,
                Purl = $"pkg:nuget/{include}@{version}"
            };
        }
    }

    private Dictionary<string, string> LoadCentralVersions(string repositoryRoot, string projectPath)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Search for Directory.Packages.props up the directory tree
        var dir = Path.GetDirectoryName(Path.Combine(repositoryRoot, projectPath));
        while (!string.IsNullOrEmpty(dir) && dir.StartsWith(repositoryRoot))
        {
            var propsPath = Path.Combine(dir, "Directory.Packages.props");
            if (File.Exists(propsPath))
            {
                try
                {
                    var content = File.ReadAllText(propsPath);
                    var doc = XDocument.Parse(content);

                    foreach (var pkgVer in doc.Descendants()
                        .Where(e => e.Name.LocalName == "PackageVersion"))
                    {
                        var include = pkgVer.Attribute("Include")?.Value;
                        var version = pkgVer.Attribute("Version")?.Value;

                        if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version))
                        {
                            versions[include] = version;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse {Path}", propsPath);
                }
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }

        return versions;
    }

    private IEnumerable<Package> ParseDirectoryPackagesProps(string content)
    {
        var doc = XDocument.Parse(content);

        foreach (var pkgVer in doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageVersion"))
        {
            var include = pkgVer.Attribute("Include")?.Value;
            var version = pkgVer.Attribute("Version")?.Value;

            if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version))
            {
                yield return new Package
                {
                    Name = include,
                    Version = version,
                    Ecosystem = PackageEcosystem.NuGet,
                    IsDirect = true,
                    Purl = $"pkg:nuget/{include}@{version}"
                };
            }
        }
    }

    public async Task<IReadOnlyList<Package>> ResolveTransitiveDependenciesAsync(
        IReadOnlyList<Package> packages,
        CancellationToken cancellationToken = default)
    {
        // Check if packages came from a lock file (they'll have content hashes and dependencies already)
        var allFromLockFile = packages.All(p => 
            !string.IsNullOrEmpty(p.Sha256) || p.Dependencies.Count > 0);

        if (allFromLockFile)
        {
            // Lock file already contains complete dependency tree - no API calls needed
            _logger.LogDebug("Packages from lock file - skipping NuGet API resolution");
            return packages.ToList();
        }

        // No lock file - need to resolve from NuGet API (fallback behavior)
        _logger.LogDebug("No lock file - resolving dependencies from NuGet API");
        return await ResolveFromNuGetApiAsync(packages, cancellationToken);
    }

    private async Task<IReadOnlyList<Package>> ResolveFromNuGetApiAsync(
        IReadOnlyList<Package> packages,
        CancellationToken cancellationToken)
    {
        var allPackages = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        var toResolve = new Queue<Package>(packages);

        var cache = new SourceCacheContext();
        var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);

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
                // Parse version
                if (!global::NuGet.Versioning.NuGetVersion.TryParse(package.Version, out var nugetVersion))
                {
                    _logger.LogWarning("Invalid version {Version} for {Package}", package.Version, package.Name);
                    continue;
                }

                // Get package dependencies
                var dependencyInfo = await resource.GetDependencyInfoAsync(
                    package.Name,
                    nugetVersion,
                    cache,
                    NullLogger.Instance,
                    cancellationToken);

                if (dependencyInfo == null)
                {
                    _logger.LogWarning("Package not found: {Package}", key);
                    continue;
                }

                // Update package metadata
                package.DownloadUrl = $"https://api.nuget.org/v3-flatcontainer/{package.Name.ToLowerInvariant()}/{package.Version.ToLowerInvariant()}/{package.Name.ToLowerInvariant()}.{package.Version.ToLowerInvariant()}.nupkg";

                // Get the best matching framework dependencies
                var tfmDeps = dependencyInfo.DependencyGroups
                    .OrderByDescending(g => g.TargetFramework?.Version ?? new Version(0, 0))
                    .FirstOrDefault();

                if (tfmDeps != null)
                {
                    foreach (var dep in tfmDeps.Packages)
                    {
                        // Use the minimum version from the range
                        var depVersion = dep.VersionRange.MinVersion?.ToString() ?? "0.0.0";
                        var depKey = $"{dep.Id}@{depVersion}";

                        // Add to this package's dependencies list
                        package.Dependencies.Add(new PackageDependency
                        {
                            Name = dep.Id,
                            VersionRange = dep.VersionRange.ToString(),
                            ResolvedVersion = depVersion
                        });

                        if (!allPackages.ContainsKey(depKey))
                        {
                            toResolve.Enqueue(new Package
                            {
                                Name = dep.Id,
                                Version = depVersion,
                                Ecosystem = PackageEcosystem.NuGet,
                                IsDirect = false,
                                Purl = $"pkg:nuget/{dep.Id}@{depVersion}"
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
