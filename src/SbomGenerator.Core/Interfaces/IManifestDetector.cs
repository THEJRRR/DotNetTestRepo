using SbomGenerator.Core.Models;

namespace SbomGenerator.Core.Interfaces;

/// <summary>
/// Interface for detecting package manifest files in a repository.
/// </summary>
public interface IManifestDetector
{
    /// <summary>
    /// Scans a repository directory for package manifest files.
    /// </summary>
    /// <param name="repositoryPath">Path to the repository root.</param>
    /// <param name="ecosystems">Optional filter for specific ecosystems.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of detected manifest files.</returns>
    Task<IReadOnlyList<DetectedManifest>> DetectAsync(
        string repositoryPath,
        IEnumerable<PackageEcosystem>? ecosystems = null,
        CancellationToken cancellationToken = default);
}
