namespace SbomGenerator.Core.Models;

/// <summary>
/// Represents a software package with its metadata.
/// </summary>
public class Package
{
    /// <summary>
    /// The name of the package.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The version of the package.
    /// </summary>
    public required string Version { get; set; }

    /// <summary>
    /// The package ecosystem (npm, nuget, pypi, maven, cargo, go, rubygems).
    /// </summary>
    public required PackageEcosystem Ecosystem { get; set; }

    /// <summary>
    /// The Package URL (PURL) for this package.
    /// </summary>
    public string? Purl { get; set; }

    /// <summary>
    /// The license expression (SPDX format).
    /// </summary>
    public string? License { get; set; }

    /// <summary>
    /// Package description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Homepage or repository URL.
    /// </summary>
    public string? Homepage { get; set; }

    /// <summary>
    /// The author or publisher of the package.
    /// </summary>
    public string? Author { get; set; }

    /// <summary>
    /// SHA256 hash of the package archive.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// Download URL for the package.
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// Whether this is a direct dependency (true) or transitive (false).
    /// </summary>
    public bool IsDirect { get; set; }

    /// <summary>
    /// List of files contained in this package.
    /// </summary>
    public List<PackageFile> Files { get; set; } = [];

    /// <summary>
    /// Dependencies of this package.
    /// </summary>
    public List<PackageDependency> Dependencies { get; set; } = [];

    /// <summary>
    /// Generates the PURL for this package based on ecosystem and name/version.
    /// </summary>
    public string GeneratePurl()
    {
        var type = Ecosystem switch
        {
            PackageEcosystem.Npm => "npm",
            PackageEcosystem.NuGet => "nuget",
            PackageEcosystem.PyPI => "pypi",
            PackageEcosystem.Maven => "maven",
            PackageEcosystem.Cargo => "cargo",
            PackageEcosystem.Go => "golang",
            PackageEcosystem.RubyGems => "gem",
            _ => "generic"
        };

        return $"pkg:{type}/{Name}@{Version}";
    }
}

/// <summary>
/// Represents a dependency reference.
/// </summary>
public class PackageDependency
{
    public required string Name { get; set; }
    public string? VersionRange { get; set; }
    public string? ResolvedVersion { get; set; }
}

/// <summary>
/// Represents a file within a package.
/// </summary>
public class PackageFile
{
    /// <summary>
    /// Relative path of the file within the package.
    /// </summary>
    public required string Path { get; set; }

    /// <summary>
    /// SHA256 hash of the file contents.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long? Size { get; set; }
}

/// <summary>
/// Supported package ecosystems.
/// </summary>
public enum PackageEcosystem
{
    Npm,
    NuGet,
    PyPI,
    Maven,
    Cargo,
    Go,
    RubyGems,
    Unknown
}
