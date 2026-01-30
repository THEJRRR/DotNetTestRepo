namespace SbomGenerator.Core.Models;

/// <summary>
/// Supported SBOM output formats.
/// </summary>
public enum SbomFormat
{
    /// <summary>
    /// SPDX version 2.2.3 JSON format.
    /// </summary>
    Spdx22,

    /// <summary>
    /// SPDX version 3.0.1 JSON-LD format.
    /// </summary>
    Spdx30,

    /// <summary>
    /// CycloneDX 1.5+ JSON format.
    /// </summary>
    CycloneDx
}

/// <summary>
/// Options for SBOM generation.
/// </summary>
public class SbomGenerationOptions
{
    /// <summary>
    /// The output format for the SBOM.
    /// </summary>
    public SbomFormat Format { get; set; } = SbomFormat.Spdx22;

    /// <summary>
    /// Whether to include file listings from packages.
    /// </summary>
    public bool IncludeFiles { get; set; } = true;

    /// <summary>
    /// Whether to resolve and include transitive dependencies.
    /// </summary>
    public bool IncludeTransitive { get; set; } = true;

    /// <summary>
    /// Filter to specific ecosystems (null = all ecosystems).
    /// </summary>
    public List<PackageEcosystem>? Ecosystems { get; set; }

    /// <summary>
    /// Name of the tool generating the SBOM.
    /// </summary>
    public string ToolName { get; set; } = "sbom-generator";

    /// <summary>
    /// Version of the tool generating the SBOM.
    /// </summary>
    public string ToolVersion { get; set; } = "1.0.0";

    /// <summary>
    /// Creator/organization name for the SBOM.
    /// </summary>
    public string? CreatorName { get; set; }
}
