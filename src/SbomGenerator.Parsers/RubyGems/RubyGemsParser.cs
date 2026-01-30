using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Parsers.RubyGems;

/// <summary>
/// Parser for Ruby Gemfile and Gemfile.lock files.
/// </summary>
public partial class RubyGemsParser : IPackageParser
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RubyGemsParser> _logger;

    public PackageEcosystem Ecosystem => PackageEcosystem.RubyGems;

    public IReadOnlyList<string> SupportedPatterns => ["Gemfile", "Gemfile.lock", "*.gemspec"];

    public RubyGemsParser(IHttpClientFactory httpClientFactory, ILogger<RubyGemsParser> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool CanParse(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName is "Gemfile" or "Gemfile.lock" || fileName.EndsWith(".gemspec");
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
            "Gemfile.lock" => ParseGemfileLock(fileContent),
            "Gemfile" => ParseGemfile(fileContent),
            _ when fileName.EndsWith(".gemspec") => ParseGemspec(fileContent),
            _ => []
        };
    }

    private List<Package> ParseGemfileLock(string content)
    {
        var packageMap = new Dictionary<string, Package>();
        var packageDeps = new Dictionary<string, List<string>>(); // package key -> dep names
        var inGemSection = false;
        var inSpecs = false;
        Package? currentPackage = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine;

            if (line.TrimStart().StartsWith("GEM"))
            {
                inGemSection = true;
                continue;
            }

            if (line.TrimStart().StartsWith("PLATFORMS") ||
                line.TrimStart().StartsWith("DEPENDENCIES") ||
                line.TrimStart().StartsWith("BUNDLED"))
            {
                inGemSection = false;
                inSpecs = false;
                currentPackage = null;
                continue;
            }

            if (inGemSection && line.TrimStart().StartsWith("specs:"))
            {
                inSpecs = true;
                continue;
            }

            if (inSpecs)
            {
                // Gems are indented with 4 spaces, sub-dependencies with 6
                if (line.StartsWith("    ") && !line.StartsWith("      "))
                {
                    // This is a gem definition
                    var match = GemLockLineRegex().Match(line);
                    if (match.Success)
                    {
                        var name = match.Groups["name"].Value;
                        var version = match.Groups["version"].Value;
                        var pkg = CreatePackage(name, version);
                        var key = $"{name}@{version}";
                        packageMap[key] = pkg;
                        currentPackage = pkg;
                    }
                }
                else if (line.StartsWith("      ") && currentPackage != null)
                {
                    // This is a dependency of the current gem (6 spaces indent)
                    var depMatch = GemDepLineRegex().Match(line);
                    if (depMatch.Success)
                    {
                        var depName = depMatch.Groups["name"].Value;
                        var depVersionRange = depMatch.Groups["version"].Success 
                            ? depMatch.Groups["version"].Value 
                            : null;

                        currentPackage.Dependencies.Add(new PackageDependency
                        {
                            Name = depName,
                            VersionRange = depVersionRange,
                            ResolvedVersion = null // Will be resolved in second pass
                        });
                    }
                }
            }
        }

        // Second pass: Resolve dependency versions
        foreach (var package in packageMap.Values)
        {
            foreach (var dep in package.Dependencies)
            {
                var resolvedPkg = packageMap.Values.FirstOrDefault(p => p.Name == dep.Name);
                if (resolvedPkg != null)
                {
                    dep.ResolvedVersion = resolvedPkg.Version;
                }
            }
        }

        return packageMap.Values.ToList();
    }

    [GeneratedRegex(@"^\s{6}(?<name>[a-zA-Z0-9_\-]+)(?:\s+\((?<version>[^\)]+)\))?")]
    private static partial Regex GemDepLineRegex();

    private List<Package> ParseGemfile(string content)
    {
        var packages = new List<Package>();

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();

            // Skip comments and empty lines
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            // Match gem 'name', 'version' or gem "name", "version"
            var match = GemfileLineRegex().Match(line);
            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                var version = match.Groups["version"].Success
                    ? match.Groups["version"].Value.TrimStart('~', '>', '<', '=', ' ')
                    : "latest";

                packages.Add(CreatePackage(name, version));
            }
        }

        return packages;
    }

    private List<Package> ParseGemspec(string content)
    {
        var packages = new List<Package>();

        // Match add_dependency and add_runtime_dependency
        var depMatches = GemspecDependencyRegex().Matches(content);
        foreach (Match match in depMatches)
        {
            var name = match.Groups["name"].Value;
            var version = match.Groups["version"].Success
                ? match.Groups["version"].Value.TrimStart('~', '>', '<', '=', ' ')
                : "latest";

            packages.Add(CreatePackage(name, version));
        }

        return packages;
    }

    private Package CreatePackage(string name, string version)
    {
        return new Package
        {
            Name = name,
            Version = version,
            Ecosystem = PackageEcosystem.RubyGems,
            IsDirect = true,
            Purl = $"pkg:gem/{name}@{version}",
            DownloadUrl = $"https://rubygems.org/downloads/{name}-{version}.gem"
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
                    $"https://rubygems.org/api/v1/versions/{package.Name}.json",
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var versions = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement[]>(cancellationToken);
                    var versionInfo = versions?.FirstOrDefault(v =>
                        v.TryGetProperty("number", out var num) && num.GetString() == package.Version);

                    if (versionInfo.HasValue)
                    {
                        if (versionInfo.Value.TryGetProperty("licenses", out var licenses) &&
                            licenses.GetArrayLength() > 0)
                        {
                            package.License = licenses[0].GetString();
                        }

                        if (versionInfo.Value.TryGetProperty("summary", out var summary))
                        {
                            package.Description = summary.GetString();
                        }

                        if (versionInfo.Value.TryGetProperty("sha", out var sha))
                        {
                            package.Sha256 = sha.GetString();
                        }
                    }
                }

                // Get more info from gem info endpoint
                var infoResponse = await httpClient.GetAsync(
                    $"https://rubygems.org/api/v1/gems/{package.Name}.json",
                    cancellationToken);

                if (infoResponse.IsSuccessStatusCode)
                {
                    var info = await infoResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);

                    if (info.TryGetProperty("homepage_uri", out var homepage))
                    {
                        package.Homepage = homepage.GetString();
                    }

                    if (info.TryGetProperty("authors", out var authors))
                    {
                        package.Author = authors.GetString();
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

    [GeneratedRegex(@"^\s{4}(?<name>[a-zA-Z0-9_\-]+)\s+\((?<version>[^\)]+)\)")]
    private static partial Regex GemLockLineRegex();

    [GeneratedRegex(@"gem\s+['""](?<name>[^'""]+)['""](?:\s*,\s*['""](?<version>[^'""]+)['""])?")]
    private static partial Regex GemfileLineRegex();

    [GeneratedRegex(@"add_(?:runtime_)?dependency\s+['""](?<name>[^'""]+)['""](?:\s*,\s*['""](?<version>[^'""]+)['""])?")]
    private static partial Regex GemspecDependencyRegex();
}
