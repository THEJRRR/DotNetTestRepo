using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Parsers.PyPI;

/// <summary>
/// Parser for Python package files (requirements.txt, pyproject.toml, etc.)
/// </summary>
public partial class PyPIParser : IPackageParser
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PyPIParser> _logger;

    public PackageEcosystem Ecosystem => PackageEcosystem.PyPI;

    public IReadOnlyList<string> SupportedPatterns =>
        ["requirements.txt", "requirements-*.txt", "pyproject.toml", "setup.py", "Pipfile", "Pipfile.lock"];

    public PyPIParser(IHttpClientFactory httpClientFactory, ILogger<PyPIParser> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool CanParse(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName == "requirements.txt" ||
               fileName.StartsWith("requirements-") && fileName.EndsWith(".txt") ||
               fileName == "pyproject.toml" ||
               fileName == "setup.py" ||
               fileName == "Pipfile" ||
               fileName == "Pipfile.lock";
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
            "requirements.txt" => ParseRequirementsTxt(fileContent),
            _ when fileName.StartsWith("requirements-") => ParseRequirementsTxt(fileContent),
            "pyproject.toml" => ParsePyprojectToml(fileContent),
            "Pipfile" => ParsePipfile(fileContent),
            "Pipfile.lock" => ParsePipfileLock(fileContent),
            _ => []
        };
    }

    private List<Package> ParseRequirementsTxt(string content)
    {
        var packages = new List<Package>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Skip comments and empty lines
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            // Skip -r, -e, --index-url, etc.
            if (line.StartsWith('-'))
                continue;

            // Parse package==version, package>=version, etc.
            var match = RequirementRegex().Match(line);
            if (match.Success)
            {
                var name = match.Groups["name"].Value.Trim();
                var version = match.Groups["version"].Value.Trim();

                // Normalize package name (PEP 503)
                name = NormalizePyPIName(name);

                packages.Add(new Package
                {
                    Name = name,
                    Version = string.IsNullOrEmpty(version) ? "latest" : version,
                    Ecosystem = PackageEcosystem.PyPI,
                    IsDirect = true,
                    Purl = $"pkg:pypi/{name}@{version}"
                });
            }
        }

        return packages;
    }

    private List<Package> ParsePyprojectToml(string content)
    {
        var packages = new List<Package>();

        // Simple TOML parsing for dependencies section
        var inDependencies = false;
        var lines = content.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.StartsWith("[project.dependencies]") || line.StartsWith("[tool.poetry.dependencies]"))
            {
                inDependencies = true;
                continue;
            }

            if (line.StartsWith('[') && inDependencies)
            {
                inDependencies = false;
                continue;
            }

            if (inDependencies && !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            {
                // Handle array format: "package>=1.0"
                if (line.StartsWith('"') || line.StartsWith('\''))
                {
                    var dep = line.Trim('"', '\'', ',', ' ');
                    var match = RequirementRegex().Match(dep);
                    if (match.Success)
                    {
                        var name = NormalizePyPIName(match.Groups["name"].Value);
                        var version = match.Groups["version"].Value;
                        packages.Add(CreatePackage(name, version));
                    }
                }
                // Handle table format: package = "^1.0"
                else if (line.Contains('='))
                {
                    var parts = line.Split('=', 2);
                    var name = NormalizePyPIName(parts[0].Trim());
                    var version = parts[1].Trim().Trim('"', '\'', '^', '~');
                    packages.Add(CreatePackage(name, version));
                }
            }
        }

        // Also parse dependencies array format
        var depsMatch = DependenciesArrayRegex().Match(content);
        if (depsMatch.Success)
        {
            var depsContent = depsMatch.Groups[1].Value;
            var depMatches = DependencyLineRegex().Matches(depsContent);
            foreach (Match depMatch in depMatches)
            {
                var dep = depMatch.Groups[1].Value;
                var reqMatch = RequirementRegex().Match(dep);
                if (reqMatch.Success)
                {
                    var name = NormalizePyPIName(reqMatch.Groups["name"].Value);
                    var version = reqMatch.Groups["version"].Value;
                    packages.Add(CreatePackage(name, version));
                }
            }
        }

        return packages;
    }

    private List<Package> ParsePipfile(string content)
    {
        var packages = new List<Package>();
        var inPackages = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line == "[packages]" || line == "[dev-packages]")
            {
                inPackages = true;
                continue;
            }

            if (line.StartsWith('['))
            {
                inPackages = false;
                continue;
            }

            if (inPackages && line.Contains('=') && !line.StartsWith('#'))
            {
                var parts = line.Split('=', 2);
                var name = NormalizePyPIName(parts[0].Trim());
                var version = parts[1].Trim().Trim('"', '\'', '*');

                packages.Add(CreatePackage(name, string.IsNullOrEmpty(version) ? "latest" : version));
            }
        }

        return packages;
    }

    private List<Package> ParsePipfileLock(string content)
    {
        var packages = new List<Package>();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            var root = doc.RootElement;

            foreach (var section in new[] { "default", "develop" })
            {
                if (root.TryGetProperty(section, out var deps))
                {
                    foreach (var dep in deps.EnumerateObject())
                    {
                        var name = NormalizePyPIName(dep.Name);
                        var version = dep.Value.TryGetProperty("version", out var v)
                            ? v.GetString()?.TrimStart('=') ?? "latest"
                            : "latest";

                        var pkg = CreatePackage(name, version);

                        if (dep.Value.TryGetProperty("hashes", out var hashes))
                        {
                            foreach (var hash in hashes.EnumerateArray())
                            {
                                var hashStr = hash.GetString();
                                if (hashStr?.StartsWith("sha256:") == true)
                                {
                                    pkg.Sha256 = hashStr["sha256:".Length..];
                                    break;
                                }
                            }
                        }

                        packages.Add(pkg);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Pipfile.lock");
        }

        return packages;
    }

    private Package CreatePackage(string name, string version)
    {
        return new Package
        {
            Name = name,
            Version = version,
            Ecosystem = PackageEcosystem.PyPI,
            IsDirect = true,
            Purl = $"pkg:pypi/{name}@{version}"
        };
    }

    private static string NormalizePyPIName(string name)
    {
        // PEP 503: normalize package names
        return name.ToLowerInvariant().Replace('_', '-').Replace('.', '-');
    }

    public async Task<IReadOnlyList<Package>> ResolveTransitiveDependenciesAsync(
        IReadOnlyList<Package> packages,
        CancellationToken cancellationToken = default)
    {
        var allPackages = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        var toResolve = new Queue<Package>(packages);

        using var httpClient = _httpClientFactory.CreateClient();

        while (toResolve.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var package = toResolve.Dequeue();
            var key = $"{package.Name}@{package.Version}";

            if (allPackages.ContainsKey(key))
                continue;

            allPackages[key] = package;

            try
            {
                var response = await httpClient.GetAsync(
                    $"https://pypi.org/pypi/{package.Name}/{package.Version}/json",
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to fetch {Package}: {Status}", key, response.StatusCode);
                    continue;
                }

                var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);

                // Get metadata
                if (json.TryGetProperty("info", out var info))
                {
                    if (info.TryGetProperty("license", out var license))
                        package.License = license.GetString();

                    if (info.TryGetProperty("summary", out var summary))
                        package.Description = summary.GetString();

                    if (info.TryGetProperty("home_page", out var homepage))
                        package.Homepage = homepage.GetString();

                    if (info.TryGetProperty("author", out var author))
                        package.Author = author.GetString();

                    // Parse requires_dist for dependencies
                    if (info.TryGetProperty("requires_dist", out var requiresDist) &&
                        requiresDist.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var req in requiresDist.EnumerateArray())
                        {
                            var reqStr = req.GetString();
                            if (string.IsNullOrEmpty(reqStr)) continue;

                            // Skip extras and environment markers
                            if (reqStr.Contains(';')) continue;

                            var match = RequirementRegex().Match(reqStr);
                            if (match.Success)
                            {
                                var depName = NormalizePyPIName(match.Groups["name"].Value);
                                var depVersion = match.Groups["version"].Value;
                                if (string.IsNullOrEmpty(depVersion)) depVersion = "latest";

                                var depKey = $"{depName}@{depVersion}";
                                
                                // Add to this package's dependencies list
                                package.Dependencies.Add(new PackageDependency
                                {
                                    Name = depName,
                                    VersionRange = match.Groups["version"].Value,
                                    ResolvedVersion = depVersion
                                });

                                if (!allPackages.ContainsKey(depKey))
                                {
                                    toResolve.Enqueue(new Package
                                    {
                                        Name = depName,
                                        Version = depVersion,
                                        Ecosystem = PackageEcosystem.PyPI,
                                        IsDirect = false,
                                        Purl = $"pkg:pypi/{depName}@{depVersion}"
                                    });
                                }
                            }
                        }
                    }
                }

                // Get download URL
                if (json.TryGetProperty("urls", out var urls) && urls.GetArrayLength() > 0)
                {
                    foreach (var url in urls.EnumerateArray())
                    {
                        if (url.TryGetProperty("packagetype", out var pkgType) &&
                            pkgType.GetString() == "bdist_wheel")
                        {
                            if (url.TryGetProperty("url", out var downloadUrl))
                                package.DownloadUrl = downloadUrl.GetString();
                            if (url.TryGetProperty("digests", out var digests) &&
                                digests.TryGetProperty("sha256", out var sha256))
                                package.Sha256 = sha256.GetString();
                            break;
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

    [GeneratedRegex(@"^(?<name>[a-zA-Z0-9_\-\.]+)\s*(?:[<>=!~]+\s*(?<version>[a-zA-Z0-9\.\-\*]+))?")]
    private static partial Regex RequirementRegex();

    [GeneratedRegex(@"dependencies\s*=\s*\[([\s\S]*?)\]")]
    private static partial Regex DependenciesArrayRegex();

    [GeneratedRegex(@"""([^""]+)""")]
    private static partial Regex DependencyLineRegex();
}
