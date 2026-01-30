using System.Text.Json;
using System.Text.Json.Serialization;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Generators.Spdx22;

/// <summary>
/// Generates SPDX 2.2.3 format SBOMs.
/// </summary>
public class Spdx22Generator : ISbomGenerator
{
    public SbomFormat Format => SbomFormat.Spdx22;

    public Task<string> GenerateAsync(
        RepositoryAnalysis analysis,
        SbomGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        var document = new Spdx22Document
        {
            SpdxVersion = "SPDX-2.3",
            DataLicense = "CC0-1.0",
            SPDXID = "SPDXRef-DOCUMENT",
            Name = GetDocumentName(analysis.RepositoryUrl),
            DocumentNamespace = $"https://spdx.org/spdxdocs/{Guid.NewGuid()}",
            CreationInfo = new CreationInfo
            {
                Created = analysis.AnalyzedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Creators = [
                    $"Tool: {options.ToolName}-{options.ToolVersion}",
                    options.CreatorName != null ? $"Organization: {options.CreatorName}" : "Tool: sbom-generator"
                ]
            },
            Packages = analysis.Packages.Select((p, i) => CreatePackage(p, i)).ToList(),
            Relationships = CreateRelationships(analysis.Packages)
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(document, jsonOptions);
        return Task.FromResult(json);
    }

    private static string GetDocumentName(string repoUrl)
    {
        var uri = new Uri(repoUrl.TrimEnd('/'));
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length >= 2 ? $"{parts[^2]}-{parts[^1]}" : parts[^1];
    }

    private static Spdx22Package CreatePackage(Package pkg, int index)
    {
        var spdxId = $"SPDXRef-Package-{index + 1}";

        var spdxPackage = new Spdx22Package
        {
            SPDXID = spdxId,
            Name = pkg.Name,
            VersionInfo = pkg.Version,
            DownloadLocation = pkg.DownloadUrl ?? "NOASSERTION",
            FilesAnalyzed = pkg.Files.Count > 0,
            LicenseConcluded = pkg.License ?? "NOASSERTION",
            LicenseDeclared = pkg.License ?? "NOASSERTION",
            CopyrightText = "NOASSERTION",
            Description = pkg.Description,
            Homepage = pkg.Homepage,
            Supplier = pkg.Author != null ? $"Person: {pkg.Author}" : null,
            // Indicate dependency type using primaryPackagePurpose and comment
            PrimaryPackagePurpose = pkg.IsDirect ? "APPLICATION" : "LIBRARY",
            Comment = pkg.IsDirect ? "Direct dependency" : "Transitive dependency",
            ExternalRefs = [
                new ExternalRef
                {
                    ReferenceCategory = "PACKAGE-MANAGER",
                    ReferenceType = "purl",
                    ReferenceLocator = pkg.Purl ?? pkg.GeneratePurl()
                }
            ]
        };

        // Add checksums
        if (!string.IsNullOrEmpty(pkg.Sha256))
        {
            spdxPackage.Checksums = [
                new Checksum
                {
                    Algorithm = "SHA256",
                    ChecksumValue = pkg.Sha256
                }
            ];
        }

        // Add files
        if (pkg.Files.Count > 0)
        {
            spdxPackage.HasFiles = pkg.Files.Select((f, i) =>
                $"SPDXRef-File-{index + 1}-{i + 1}").ToList();
        }

        return spdxPackage;
    }

    private static List<Spdx22Relationship> CreateRelationships(List<Package> packages)
    {
        var relationships = new List<Spdx22Relationship>();

        // Document DESCRIBES direct dependencies only
        // Transitive dependencies use DEPENDENCY_OF relationship
        for (var i = 0; i < packages.Count; i++)
        {
            var pkg = packages[i];
            
            if (pkg.IsDirect)
            {
                // Direct dependency - document describes it
                relationships.Add(new Spdx22Relationship
                {
                    SpdxElementId = "SPDXRef-DOCUMENT",
                    RelationshipType = "DESCRIBES",
                    RelatedSpdxElement = $"SPDXRef-Package-{i + 1}"
                });
            }
            else
            {
                // Transitive dependency - document contains it (but doesn't describe it directly)
                relationships.Add(new Spdx22Relationship
                {
                    SpdxElementId = $"SPDXRef-Package-{i + 1}",
                    RelationshipType = "DEPENDENCY_OF",
                    RelatedSpdxElement = "SPDXRef-DOCUMENT",
                    Comment = "Transitive dependency"
                });
            }
        }

        // Add explicit dependency relationships between packages
        for (var i = 0; i < packages.Count; i++)
        {
            var pkg = packages[i];
            foreach (var dep in pkg.Dependencies)
            {
                var depIndex = packages.FindIndex(p =>
                    p.Name == dep.Name &&
                    (p.Version == dep.ResolvedVersion || p.Version == dep.VersionRange));

                if (depIndex >= 0)
                {
                    relationships.Add(new Spdx22Relationship
                    {
                        SpdxElementId = $"SPDXRef-Package-{i + 1}",
                        RelationshipType = "DEPENDS_ON",
                        RelatedSpdxElement = $"SPDXRef-Package-{depIndex + 1}"
                    });
                }
            }
        }

        return relationships;
    }
}

// SPDX 2.2/2.3 Document Model
public class Spdx22Document
{
    [JsonPropertyName("spdxVersion")]
    public required string SpdxVersion { get; set; }

    [JsonPropertyName("dataLicense")]
    public required string DataLicense { get; set; }

    [JsonPropertyName("SPDXID")]
    public required string SPDXID { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("documentNamespace")]
    public required string DocumentNamespace { get; set; }

    [JsonPropertyName("creationInfo")]
    public required CreationInfo CreationInfo { get; set; }

    [JsonPropertyName("packages")]
    public List<Spdx22Package> Packages { get; set; } = [];

    [JsonPropertyName("relationships")]
    public List<Spdx22Relationship> Relationships { get; set; } = [];
}

public class CreationInfo
{
    [JsonPropertyName("created")]
    public required string Created { get; set; }

    [JsonPropertyName("creators")]
    public List<string> Creators { get; set; } = [];
}

public class Spdx22Package
{
    [JsonPropertyName("SPDXID")]
    public required string SPDXID { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("versionInfo")]
    public required string VersionInfo { get; set; }

    [JsonPropertyName("downloadLocation")]
    public required string DownloadLocation { get; set; }

    [JsonPropertyName("filesAnalyzed")]
    public bool FilesAnalyzed { get; set; }

    [JsonPropertyName("primaryPackagePurpose")]
    public string? PrimaryPackagePurpose { get; set; }

    [JsonPropertyName("licenseConcluded")]
    public required string LicenseConcluded { get; set; }

    [JsonPropertyName("licenseDeclared")]
    public required string LicenseDeclared { get; set; }

    [JsonPropertyName("copyrightText")]
    public required string CopyrightText { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    [JsonPropertyName("supplier")]
    public string? Supplier { get; set; }

    [JsonPropertyName("checksums")]
    public List<Checksum>? Checksums { get; set; }

    [JsonPropertyName("externalRefs")]
    public List<ExternalRef>? ExternalRefs { get; set; }

    [JsonPropertyName("hasFiles")]
    public List<string>? HasFiles { get; set; }
}

public class Checksum
{
    [JsonPropertyName("algorithm")]
    public required string Algorithm { get; set; }

    [JsonPropertyName("checksumValue")]
    public required string ChecksumValue { get; set; }
}

public class ExternalRef
{
    [JsonPropertyName("referenceCategory")]
    public required string ReferenceCategory { get; set; }

    [JsonPropertyName("referenceType")]
    public required string ReferenceType { get; set; }

    [JsonPropertyName("referenceLocator")]
    public required string ReferenceLocator { get; set; }
}

public class Spdx22Relationship
{
    [JsonPropertyName("spdxElementId")]
    public required string SpdxElementId { get; set; }

    [JsonPropertyName("relationshipType")]
    public required string RelationshipType { get; set; }

    [JsonPropertyName("relatedSpdxElement")]
    public required string RelatedSpdxElement { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}
