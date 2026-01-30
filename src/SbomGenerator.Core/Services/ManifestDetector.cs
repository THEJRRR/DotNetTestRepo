using Microsoft.Extensions.Logging;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Core.Services;

/// <summary>
/// Detects package manifest files in a repository.
/// </summary>
public class ManifestDetector : IManifestDetector
{
    private readonly ILogger<ManifestDetector> _logger;

    // Map of file patterns to ecosystems
    private static readonly Dictionary<string, (PackageEcosystem Ecosystem, string ManifestType)> ManifestPatterns = new()
    {
        // NPM
        { "package.json", (PackageEcosystem.Npm, "package.json") },
        { "package-lock.json", (PackageEcosystem.Npm, "package-lock.json") },

        // NuGet
        { "*.csproj", (PackageEcosystem.NuGet, "csproj") },
        { "*.fsproj", (PackageEcosystem.NuGet, "fsproj") },
        { "*.vbproj", (PackageEcosystem.NuGet, "vbproj") },
        { "packages.config", (PackageEcosystem.NuGet, "packages.config") },
        { "Directory.Packages.props", (PackageEcosystem.NuGet, "Directory.Packages.props") },

        // PyPI
        { "requirements.txt", (PackageEcosystem.PyPI, "requirements.txt") },
        { "requirements-*.txt", (PackageEcosystem.PyPI, "requirements.txt") },
        { "pyproject.toml", (PackageEcosystem.PyPI, "pyproject.toml") },
        { "setup.py", (PackageEcosystem.PyPI, "setup.py") },
        { "Pipfile", (PackageEcosystem.PyPI, "Pipfile") },
        { "Pipfile.lock", (PackageEcosystem.PyPI, "Pipfile.lock") },

        // Maven
        { "pom.xml", (PackageEcosystem.Maven, "pom.xml") },
        { "build.gradle", (PackageEcosystem.Maven, "build.gradle") },
        { "build.gradle.kts", (PackageEcosystem.Maven, "build.gradle.kts") },

        // Cargo
        { "Cargo.toml", (PackageEcosystem.Cargo, "Cargo.toml") },
        { "Cargo.lock", (PackageEcosystem.Cargo, "Cargo.lock") },

        // Go
        { "go.mod", (PackageEcosystem.Go, "go.mod") },
        { "go.sum", (PackageEcosystem.Go, "go.sum") },

        // RubyGems
        { "Gemfile", (PackageEcosystem.RubyGems, "Gemfile") },
        { "Gemfile.lock", (PackageEcosystem.RubyGems, "Gemfile.lock") },
        { "*.gemspec", (PackageEcosystem.RubyGems, "gemspec") }
    };

    public ManifestDetector(ILogger<ManifestDetector> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<DetectedManifest>> DetectAsync(
        string repositoryPath,
        IEnumerable<PackageEcosystem>? ecosystems = null,
        CancellationToken cancellationToken = default)
    {
        var allowedEcosystems = ecosystems?.ToHashSet();
        var manifests = new List<DetectedManifest>();

        foreach (var (pattern, info) in ManifestPatterns)
        {
            if (allowedEcosystems != null && !allowedEcosystems.Contains(info.Ecosystem))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var searchPattern = pattern;
                var searchOption = SearchOption.AllDirectories;

                // Find matching files
                var files = Directory.EnumerateFiles(repositoryPath, searchPattern, searchOption)
                    .Where(f => !f.Contains("node_modules") && 
                               !f.Contains(".git") && 
                               !f.Contains("bin") && 
                               !f.Contains("obj"));

                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(repositoryPath, file).Replace('\\', '/');
                    
                    _logger.LogDebug("Detected {ManifestType}: {Path}", info.ManifestType, relativePath);
                    
                    manifests.Add(new DetectedManifest
                    {
                        Path = relativePath,
                        Ecosystem = info.Ecosystem,
                        ManifestType = info.ManifestType
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error searching for {Pattern}", pattern);
            }
        }

        // Deduplicate
        var unique = manifests
            .GroupBy(m => m.Path)
            .Select(g => g.First())
            .ToList();

        return Task.FromResult<IReadOnlyList<DetectedManifest>>(unique);
    }
}
