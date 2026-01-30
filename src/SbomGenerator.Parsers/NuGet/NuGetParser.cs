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
/// Parser for NuGet project files (*.csproj, packages.config, etc.)
/// </summary>
public partial class NuGetParser : IPackageParser
{
    private readonly ILogger<NuGetParser> _logger;

    public PackageEcosystem Ecosystem => PackageEcosystem.NuGet;

    public IReadOnlyList<string> SupportedPatterns =>
        ["*.csproj", "*.fsproj", "*.vbproj", "packages.config", "Directory.Packages.props"];

    public NuGetParser(ILogger<NuGetParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext is ".csproj" or ".fsproj" or ".vbproj" ||
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

        try
        {
            if (fileName == "packages.config")
            {
                packages.AddRange(ParsePackagesConfig(fileContent));
            }
            else if (ext is ".csproj" or ".fsproj" or ".vbproj")
            {
                packages.AddRange(ParseProjectFile(fileContent, repositoryRoot, filePath));
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
