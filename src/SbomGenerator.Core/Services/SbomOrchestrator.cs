using Microsoft.Extensions.Logging;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Core.Services;

/// <summary>
/// Main orchestrator service for SBOM generation.
/// </summary>
public class SbomOrchestrator
{
    private readonly IRepositoryCloner _repositoryCloner;
    private readonly IManifestDetector _manifestDetector;
    private readonly IEnumerable<IPackageParser> _parsers;
    private readonly IEnumerable<IPackageFileExtractor> _extractors;
    private readonly IEnumerable<ISbomGenerator> _generators;
    private readonly ILogger<SbomOrchestrator> _logger;

    public SbomOrchestrator(
        IRepositoryCloner repositoryCloner,
        IManifestDetector manifestDetector,
        IEnumerable<IPackageParser> parsers,
        IEnumerable<IPackageFileExtractor> extractors,
        IEnumerable<ISbomGenerator> generators,
        ILogger<SbomOrchestrator> logger)
    {
        _repositoryCloner = repositoryCloner;
        _manifestDetector = manifestDetector;
        _parsers = parsers;
        _extractors = extractors;
        _generators = generators;
        _logger = logger;
    }

    /// <summary>
    /// Generates an SBOM for the given repository.
    /// </summary>
    public async Task<string> GenerateSbomAsync(
        string repositoryUrl,
        SbomGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        string? repoPath = null;

        try
        {
            // Clone the repository
            _logger.LogInformation("Cloning repository: {Url}", repositoryUrl);
            repoPath = await _repositoryCloner.CloneAsync(repositoryUrl, cancellationToken: cancellationToken);

            // Analyze the repository
            var analysis = await AnalyzeRepositoryAsync(repoPath, repositoryUrl, options, cancellationToken);

            // Extract file listings if requested
            if (options.IncludeFiles)
            {
                await ExtractPackageFilesAsync(analysis, cancellationToken);
            }

            // Generate the SBOM
            var generator = _generators.FirstOrDefault(g => g.Format == options.Format)
                ?? throw new InvalidOperationException($"No generator found for format: {options.Format}");

            _logger.LogInformation("Generating SBOM in {Format} format", options.Format);
            return await generator.GenerateAsync(analysis, options, cancellationToken);
        }
        finally
        {
            // Clean up cloned repository
            if (repoPath != null)
            {
                _logger.LogDebug("Cleaning up cloned repository");
                _repositoryCloner.Cleanup(repoPath);
            }
        }
    }

    private async Task<RepositoryAnalysis> AnalyzeRepositoryAsync(
        string repoPath,
        string repositoryUrl,
        SbomGenerationOptions options,
        CancellationToken cancellationToken)
    {
        var analysis = new RepositoryAnalysis
        {
            RepositoryUrl = repositoryUrl
        };

        // Detect manifest files
        _logger.LogInformation("Scanning for package manifests...");
        var manifests = await _manifestDetector.DetectAsync(repoPath, options.Ecosystems, cancellationToken);
        analysis.Manifests.AddRange(manifests);

        _logger.LogInformation("Found {Count} manifest file(s)", manifests.Count);

        // Parse each manifest
        foreach (var manifest in manifests)
        {
            var parser = _parsers.FirstOrDefault(p => p.Ecosystem == manifest.Ecosystem && p.CanParse(manifest.Path));
            if (parser == null)
            {
                _logger.LogWarning("No parser found for {Path} ({Ecosystem})", manifest.Path, manifest.Ecosystem);
                continue;
            }

            try
            {
                var fullPath = Path.Combine(repoPath, manifest.Path);
                var content = await File.ReadAllTextAsync(fullPath, cancellationToken);

                _logger.LogDebug("Parsing {Path}", manifest.Path);
                var packages = await parser.ParseAsync(manifest.Path, content, repoPath, cancellationToken);

                // Mark as direct dependencies
                foreach (var pkg in packages)
                {
                    pkg.IsDirect = true;
                    pkg.Purl ??= pkg.GeneratePurl();
                }

                // Resolve transitive dependencies if requested
                if (options.IncludeTransitive)
                {
                    _logger.LogDebug("Resolving transitive dependencies for {Ecosystem}", manifest.Ecosystem);
                    packages = await parser.ResolveTransitiveDependenciesAsync(packages, cancellationToken);
                }

                // Add packages (avoiding duplicates)
                foreach (var pkg in packages)
                {
                    if (!analysis.Packages.Any(p => p.Purl == pkg.Purl))
                    {
                        analysis.Packages.Add(pkg);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing {Path}", manifest.Path);
                analysis.Errors.Add(new AnalysisError
                {
                    Message = ex.Message,
                    FilePath = manifest.Path,
                    Ecosystem = manifest.Ecosystem,
                    Exception = ex
                });
            }
        }

        _logger.LogInformation("Found {Count} package(s) total", analysis.Packages.Count);
        return analysis;
    }

    private async Task ExtractPackageFilesAsync(
        RepositoryAnalysis analysis,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Extracting file listings from packages...");

        foreach (var package in analysis.Packages)
        {
            var extractor = _extractors.FirstOrDefault(e => e.Ecosystem == package.Ecosystem);
            if (extractor == null)
            {
                continue;
            }

            try
            {
                var files = await extractor.ExtractFilesAsync(package, cancellationToken);
                package.Files.AddRange(files);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract files from {Package}", package.Purl);
            }
        }
    }
}
