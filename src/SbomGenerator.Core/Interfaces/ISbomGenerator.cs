using SbomGenerator.Core.Models;

namespace SbomGenerator.Core.Interfaces;

/// <summary>
/// Interface for SBOM format generators.
/// </summary>
public interface ISbomGenerator
{
    /// <summary>
    /// The format this generator produces.
    /// </summary>
    SbomFormat Format { get; }

    /// <summary>
    /// Generates an SBOM from the repository analysis.
    /// </summary>
    /// <param name="analysis">The repository analysis containing packages.</param>
    /// <param name="options">Generation options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The SBOM as a JSON string.</returns>
    Task<string> GenerateAsync(
        RepositoryAnalysis analysis,
        SbomGenerationOptions options,
        CancellationToken cancellationToken = default);
}
