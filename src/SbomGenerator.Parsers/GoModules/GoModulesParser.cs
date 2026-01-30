using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Parsers.GoModules;

/// <summary>
/// Parser for Go modules (go.mod and go.sum files).
/// </summary>
public partial class GoModulesParser : IPackageParser
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoModulesParser> _logger;

    public PackageEcosystem Ecosystem => PackageEcosystem.Go;

    public IReadOnlyList<string> SupportedPatterns => ["go.mod", "go.sum"];

    public GoModulesParser(IHttpClientFactory httpClientFactory, ILogger<GoModulesParser> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool CanParse(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName is "go.mod" or "go.sum";
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
            "go.sum" => ParseGoSum(fileContent),
            "go.mod" => ParseGoMod(fileContent),
            _ => []
        };
    }

    private List<Package> ParseGoMod(string content)
    {
        var packages = new List<Package>();
        var inRequire = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();

            // Handle single-line require
            if (line.StartsWith("require ") && !line.Contains('('))
            {
                var match = RequireLineRegex().Match(line);
                if (match.Success)
                {
                    packages.Add(CreatePackage(match.Groups["module"].Value, match.Groups["version"].Value));
                }
                continue;
            }

            // Handle require block
            if (line == "require (")
            {
                inRequire = true;
                continue;
            }

            if (line == ")")
            {
                inRequire = false;
                continue;
            }

            if (inRequire && !string.IsNullOrWhiteSpace(line) && !line.StartsWith("//"))
            {
                var match = ModuleVersionRegex().Match(line);
                if (match.Success)
                {
                    var module = match.Groups["module"].Value;
                    var version = match.Groups["version"].Value;

                    // Skip indirect dependencies if marked
                    var isDirect = !line.Contains("// indirect");

                    var pkg = CreatePackage(module, version);
                    pkg.IsDirect = isDirect;
                    packages.Add(pkg);
                }
            }
        }

        return packages;
    }

    private List<Package> ParseGoSum(string content)
    {
        var packages = new Dictionary<string, Package>();

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var match = GoSumLineRegex().Match(line);
            if (match.Success)
            {
                var module = match.Groups["module"].Value;
                var version = match.Groups["version"].Value;
                var hash = match.Groups["hash"].Value;

                // Remove /go.mod suffix if present
                if (version.EndsWith("/go.mod"))
                {
                    continue; // Skip go.mod hash entries
                }

                var key = $"{module}@{version}";
                if (!packages.ContainsKey(key))
                {
                    var pkg = CreatePackage(module, version);
                    pkg.Sha256 = hash;
                    packages[key] = pkg;
                }
            }
        }

        return packages.Values.ToList();
    }

    private Package CreatePackage(string module, string version)
    {
        return new Package
        {
            Name = module,
            Version = version,
            Ecosystem = PackageEcosystem.Go,
            IsDirect = true,
            Purl = $"pkg:golang/{module}@{version}",
            DownloadUrl = $"https://proxy.golang.org/{module}/@v/{version}.zip"
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
                // Fetch module info from Go proxy
                var encodedModule = package.Name.Replace("/", "%2F");
                var response = await httpClient.GetAsync(
                    $"https://proxy.golang.org/{encodedModule}/@v/{package.Version}.info",
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);

                    if (json.TryGetProperty("Version", out var version))
                    {
                        package.Version = version.GetString() ?? package.Version;
                    }
                }

                // Try to get license from pkg.go.dev
                var pkgResponse = await httpClient.GetAsync(
                    $"https://pkg.go.dev/{package.Name}@{package.Version}?tab=licenses",
                    cancellationToken);

                if (pkgResponse.IsSuccessStatusCode)
                {
                    var html = await pkgResponse.Content.ReadAsStringAsync(cancellationToken);
                    var licenseMatch = LicenseRegex().Match(html);
                    if (licenseMatch.Success)
                    {
                        package.License = licenseMatch.Groups["license"].Value;
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

    [GeneratedRegex(@"require\s+(?<module>\S+)\s+(?<version>\S+)")]
    private static partial Regex RequireLineRegex();

    [GeneratedRegex(@"^\s*(?<module>\S+)\s+(?<version>v\S+)")]
    private static partial Regex ModuleVersionRegex();

    [GeneratedRegex(@"^(?<module>\S+)\s+(?<version>v\S+)\s+h1:(?<hash>\S+)")]
    private static partial Regex GoSumLineRegex();

    [GeneratedRegex(@"<span[^>]*>(?<license>[^<]+)</span>", RegexOptions.IgnoreCase)]
    private static partial Regex LicenseRegex();
}
