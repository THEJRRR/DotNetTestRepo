using SbomGenerator.Core.Models;

namespace SbomGenerator.Core.Interfaces;

/// <summary>
/// Interface for package ecosystem parsers.
/// </summary>
public interface IPackageParser
{
    /// <summary>
    /// The ecosystem this parser handles.
    /// </summary>
    PackageEcosystem Ecosystem { get; }

    /// <summary>
    /// File patterns this parser can handle (e.g., "package.json", "*.csproj").
    /// </summary>
    IReadOnlyList<string> SupportedPatterns { get; }

    /// <summary>
    /// Determines if this parser can handle the given file.
    /// </summary>
    bool CanParse(string filePath);

    /// <summary>
    /// Parses a manifest file and returns discovered packages.
    /// </summary>
    /// <param name="filePath">Path to the manifest file.</param>
    /// <param name="fileContent">Content of the manifest file.</param>
    /// <param name="repositoryRoot">Root directory of the repository.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of packages found in the manifest.</returns>
    Task<IReadOnlyList<Package>> ParseAsync(
        string filePath,
        string fileContent,
        string repositoryRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves transitive dependencies for the given packages.
    /// </summary>
    /// <param name="packages">Direct packages to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All packages including transitive dependencies.</returns>
    Task<IReadOnlyList<Package>> ResolveTransitiveDependenciesAsync(
        IReadOnlyList<Package> packages,
        CancellationToken cancellationToken = default);
}
