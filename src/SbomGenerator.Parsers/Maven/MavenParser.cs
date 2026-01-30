using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Parsers.Maven;

/// <summary>
/// Parser for Maven pom.xml and Gradle build files.
/// </summary>
public partial class MavenParser : IPackageParser
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MavenParser> _logger;

    public PackageEcosystem Ecosystem => PackageEcosystem.Maven;

    public IReadOnlyList<string> SupportedPatterns =>
        ["pom.xml", "build.gradle", "build.gradle.kts"];

    public MavenParser(IHttpClientFactory httpClientFactory, ILogger<MavenParser> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool CanParse(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName is "pom.xml" or "build.gradle" or "build.gradle.kts";
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
            "pom.xml" => ParsePomXml(fileContent),
            "build.gradle" => ParseBuildGradle(fileContent),
            "build.gradle.kts" => ParseBuildGradle(fileContent),
            _ => []
        };
    }

    private List<Package> ParsePomXml(string content)
    {
        var packages = new List<Package>();

        try
        {
            var doc = XDocument.Parse(content);
            XNamespace ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            // Extract properties for variable substitution
            var properties = new Dictionary<string, string>();
            var propsElement = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "properties");
            if (propsElement != null)
            {
                foreach (var prop in propsElement.Elements())
                {
                    properties[prop.Name.LocalName] = prop.Value;
                }
            }

            // Get parent version if exists
            var parentVersion = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "parent")?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == "version")?.Value;

            // Parse dependencies
            var dependencies = doc.Descendants()
                .Where(e => e.Name.LocalName == "dependency" &&
                           e.Parent?.Name.LocalName != "plugin");

            foreach (var dep in dependencies)
            {
                var groupId = dep.Elements().FirstOrDefault(e => e.Name.LocalName == "groupId")?.Value;
                var artifactId = dep.Elements().FirstOrDefault(e => e.Name.LocalName == "artifactId")?.Value;
                var version = dep.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value;

                if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(artifactId))
                    continue;

                // Resolve property references
                version = ResolveProperty(version, properties, parentVersion);
                groupId = ResolveProperty(groupId, properties, parentVersion);
                artifactId = ResolveProperty(artifactId, properties, parentVersion);

                if (string.IsNullOrEmpty(version))
                    version = "latest";

                packages.Add(new Package
                {
                    Name = $"{groupId}:{artifactId}",
                    Version = version,
                    Ecosystem = PackageEcosystem.Maven,
                    IsDirect = true,
                    Purl = $"pkg:maven/{groupId}/{artifactId}@{version}"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse pom.xml");
        }

        return packages;
    }

    private string? ResolveProperty(string? value, Dictionary<string, string> properties, string? parentVersion)
    {
        if (string.IsNullOrEmpty(value)) return value;

        // Handle ${property} references
        var match = PropertyRefRegex().Match(value);
        while (match.Success)
        {
            var propName = match.Groups[1].Value;
            string? replacement = null;

            if (propName == "project.version" || propName == "pom.version")
            {
                replacement = parentVersion;
            }
            else if (properties.TryGetValue(propName, out var propValue))
            {
                replacement = propValue;
            }

            if (replacement != null)
            {
                value = value.Replace(match.Value, replacement);
            }

            match = PropertyRefRegex().Match(value);
            if (match.Value == "${" + propName + "}") break; // Prevent infinite loop
        }

        return value;
    }

    private List<Package> ParseBuildGradle(string content)
    {
        var packages = new List<Package>();

        // Match dependency declarations like:
        // implementation 'group:artifact:version'
        // implementation "group:artifact:version"
        // implementation("group:artifact:version")
        var depMatches = GradleDependencyRegex().Matches(content);

        foreach (Match match in depMatches)
        {
            var dep = match.Groups["dep"].Value;
            var parts = dep.Split(':');

            if (parts.Length >= 2)
            {
                var groupId = parts[0];
                var artifactId = parts[1];
                var version = parts.Length >= 3 ? parts[2] : "latest";

                // Remove any trailing qualifiers like @aar
                if (version.Contains('@'))
                {
                    version = version.Split('@')[0];
                }

                packages.Add(new Package
                {
                    Name = $"{groupId}:{artifactId}",
                    Version = version,
                    Ecosystem = PackageEcosystem.Maven,
                    IsDirect = true,
                    Purl = $"pkg:maven/{groupId}/{artifactId}@{version}"
                });
            }
        }

        return packages;
    }

    public async Task<IReadOnlyList<Package>> ResolveTransitiveDependenciesAsync(
        IReadOnlyList<Package> packages,
        CancellationToken cancellationToken = default)
    {
        // Maven transitive resolution is complex; returning direct deps for now
        // Full implementation would require parsing POMs recursively from Maven Central
        var allPackages = new Dictionary<string, Package>();

        using var httpClient = _httpClientFactory.CreateClient();

        foreach (var package in packages)
        {
            var key = $"{package.Name}@{package.Version}";
            if (allPackages.ContainsKey(key)) continue;

            allPackages[key] = package;

            try
            {
                var parts = package.Name.Split(':');
                if (parts.Length != 2) continue;

                var groupId = parts[0];
                var artifactId = parts[1];
                var groupPath = groupId.Replace('.', '/');

                // Fetch metadata from Maven Central
                var metadataUrl = $"https://repo1.maven.org/maven2/{groupPath}/{artifactId}/{package.Version}/{artifactId}-{package.Version}.pom";
                var response = await httpClient.GetAsync(metadataUrl, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var pomContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    var doc = XDocument.Parse(pomContent);

                    // Get license
                    var license = doc.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "licenses")?
                        .Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
                    if (license != null) package.License = license;

                    // Get description
                    var desc = doc.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "description")?.Value;
                    if (desc != null) package.Description = desc;

                    // Get URL
                    var url = doc.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "url")?.Value;
                    if (url != null) package.Homepage = url;

                    package.DownloadUrl = $"https://repo1.maven.org/maven2/{groupPath}/{artifactId}/{package.Version}/{artifactId}-{package.Version}.jar";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resolving {Package}", package.Name);
            }
        }

        return allPackages.Values.ToList();
    }

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex PropertyRefRegex();

    [GeneratedRegex(@"(?:implementation|api|compile|runtimeOnly|testImplementation|testRuntimeOnly)\s*[\(]?\s*['""](?<dep>[^'""]+)['""]")]
    private static partial Regex GradleDependencyRegex();
}
