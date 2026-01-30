using SbomGenerator.Core.Models;

namespace SbomGenerator.Core.Interfaces;

/// <summary>
/// Interface for extracting file listings from packages.
/// </summary>
public interface IPackageFileExtractor
{
    /// <summary>
    /// The ecosystem this extractor handles.
    /// </summary>
    PackageEcosystem Ecosystem { get; }

    /// <summary>
    /// Downloads a package and extracts its file listing.
    /// </summary>
    /// <param name="package">The package to extract files from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of files in the package.</returns>
    Task<IReadOnlyList<PackageFile>> ExtractFilesAsync(
        Package package,
        CancellationToken cancellationToken = default);
}
