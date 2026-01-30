namespace SbomGenerator.Core.Interfaces;

/// <summary>
/// Interface for cloning Git repositories.
/// </summary>
public interface IRepositoryCloner
{
    /// <summary>
    /// Clones a repository to a temporary directory.
    /// </summary>
    /// <param name="repositoryUrl">The Git repository URL.</param>
    /// <param name="branch">Optional branch name (default branch if null).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Path to the cloned repository.</returns>
    Task<string> CloneAsync(
        string repositoryUrl,
        string? branch = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up a previously cloned repository.
    /// </summary>
    /// <param name="repositoryPath">Path to the cloned repository.</param>
    void Cleanup(string repositoryPath);
}
