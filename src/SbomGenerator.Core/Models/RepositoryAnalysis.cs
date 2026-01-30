namespace SbomGenerator.Core.Models;

/// <summary>
/// Represents the result of parsing a repository for packages.
/// </summary>
public class RepositoryAnalysis
{
    /// <summary>
    /// The repository URL that was analyzed.
    /// </summary>
    public required string RepositoryUrl { get; set; }

    /// <summary>
    /// The branch or commit that was analyzed.
    /// </summary>
    public string? Ref { get; set; }

    /// <summary>
    /// Timestamp when the analysis was performed.
    /// </summary>
    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// All packages discovered in the repository (direct and transitive).
    /// </summary>
    public List<Package> Packages { get; set; } = [];

    /// <summary>
    /// Package manifest files that were detected and parsed.
    /// </summary>
    public List<DetectedManifest> Manifests { get; set; } = [];

    /// <summary>
    /// Any errors encountered during analysis.
    /// </summary>
    public List<AnalysisError> Errors { get; set; } = [];
}

/// <summary>
/// Represents a detected package manifest file.
/// </summary>
public class DetectedManifest
{
    /// <summary>
    /// Relative path to the manifest file.
    /// </summary>
    public required string Path { get; set; }

    /// <summary>
    /// The ecosystem this manifest belongs to.
    /// </summary>
    public required PackageEcosystem Ecosystem { get; set; }

    /// <summary>
    /// Type of manifest (e.g., "package.json", "package-lock.json", "*.csproj").
    /// </summary>
    public required string ManifestType { get; set; }
}

/// <summary>
/// Represents an error encountered during analysis.
/// </summary>
public class AnalysisError
{
    public required string Message { get; set; }
    public string? FilePath { get; set; }
    public PackageEcosystem? Ecosystem { get; set; }
    public Exception? Exception { get; set; }
}
