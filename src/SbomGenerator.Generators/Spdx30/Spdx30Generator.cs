using System.Text.Json;
using System.Text.Json.Serialization;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Generators.Spdx30;

/// <summary>
/// Generates SPDX 3.0.1 format SBOMs (JSON-LD).
/// </summary>
public class Spdx30Generator : ISbomGenerator
{
    public SbomFormat Format => SbomFormat.Spdx30;

    public Task<string> GenerateAsync(
        RepositoryAnalysis analysis,
        SbomGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        var documentId = $"urn:spdx:sbom:{Guid.NewGuid()}";
        var creationTime = analysis.AnalyzedAt.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var elements = new List<object>();

        // Create SpdxDocument
        var spdxDocument = new Dictionary<string, object>
        {
            ["@type"] = "SpdxDocument",
            ["@id"] = documentId,
            ["spdxId"] = documentId,
            ["name"] = GetDocumentName(analysis.RepositoryUrl),
            ["creationInfo"] = new Dictionary<string, object>
            {
                ["@type"] = "CreationInfo",
                ["specVersion"] = "3.0.1",
                ["created"] = creationTime,
                ["createdBy"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["@type"] = "Tool",
                        ["name"] = options.ToolName,
                        ["version"] = options.ToolVersion
                    }
                },
                ["profile"] = new List<string> { "core", "software" }
            },
            ["rootElement"] = new List<string> { documentId }
        };
        elements.Add(spdxDocument);

        // Create packages
        var packageIds = new List<string>();
        for (var i = 0; i < analysis.Packages.Count; i++)
        {
            var pkg = analysis.Packages[i];
            var packageId = $"urn:spdx:package:{Guid.NewGuid()}";
            packageIds.Add(packageId);

            var spdxPackage = new Dictionary<string, object>
            {
                ["@type"] = "software_Package",
                ["@id"] = packageId,
                ["spdxId"] = packageId,
                ["name"] = pkg.Name,
                ["software_packageVersion"] = pkg.Version,
                ["software_downloadLocation"] = pkg.DownloadUrl ?? "https://spdx.org/rdf/3.0.1/terms/Core/NoAssertion",
                // Indicate dependency type using primaryPurpose and comment
                ["software_primaryPurpose"] = pkg.IsDirect ? "application" : "library",
                ["comment"] = pkg.IsDirect ? "Direct dependency" : "Transitive dependency"
            };

            // Add PURL as external identifier
            var purl = pkg.Purl ?? pkg.GeneratePurl();
            spdxPackage["externalIdentifier"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["@type"] = "ExternalIdentifier",
                    ["externalIdentifierType"] = "packageUrl",
                    ["identifier"] = purl
                }
            };

            // Add license if available
            if (!string.IsNullOrEmpty(pkg.License))
            {
                spdxPackage["declaredLicense"] = pkg.License;
            }

            // Add description
            if (!string.IsNullOrEmpty(pkg.Description))
            {
                spdxPackage["description"] = pkg.Description;
            }

            // Add homepage
            if (!string.IsNullOrEmpty(pkg.Homepage))
            {
                spdxPackage["software_homePage"] = pkg.Homepage;
            }

            // Add checksums
            if (!string.IsNullOrEmpty(pkg.Sha256))
            {
                spdxPackage["verifiedUsing"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["@type"] = "Hash",
                        ["algorithm"] = "sha256",
                        ["hashValue"] = pkg.Sha256
                    }
                };
            }

            // Add supplier/originator
            if (!string.IsNullOrEmpty(pkg.Author))
            {
                spdxPackage["originatedBy"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["@type"] = "Person",
                        ["name"] = pkg.Author
                    }
                };
            }

            elements.Add(spdxPackage);
        }

        // Create relationships
        for (var i = 0; i < analysis.Packages.Count; i++)
        {
            var pkg = analysis.Packages[i];

            if (pkg.IsDirect)
            {
                // Direct dependency - document contains it with "dependencyType" annotation
                elements.Add(new Dictionary<string, object>
                {
                    ["@type"] = "Relationship",
                    ["@id"] = $"urn:spdx:relationship:{Guid.NewGuid()}",
                    ["relationshipType"] = "contains",
                    ["from"] = documentId,
                    ["to"] = new List<string> { packageIds[i] },
                    ["comment"] = "Direct dependency"
                });
            }
            else
            {
                // Transitive dependency - use dependencyOf relationship
                elements.Add(new Dictionary<string, object>
                {
                    ["@type"] = "Relationship",
                    ["@id"] = $"urn:spdx:relationship:{Guid.NewGuid()}",
                    ["relationshipType"] = "dependencyOf",
                    ["from"] = packageIds[i],
                    ["to"] = new List<string> { documentId },
                    ["comment"] = "Transitive dependency"
                });
            }

            // Package dependencies
            foreach (var dep in pkg.Dependencies)
            {
                var depIndex = analysis.Packages.FindIndex(p =>
                    p.Name == dep.Name &&
                    (p.Version == dep.ResolvedVersion || p.Version == dep.VersionRange));

                if (depIndex >= 0)
                {
                    elements.Add(new Dictionary<string, object>
                    {
                        ["@type"] = "Relationship",
                        ["@id"] = $"urn:spdx:relationship:{Guid.NewGuid()}",
                        ["relationshipType"] = "dependsOn",
                        ["from"] = packageIds[i],
                        ["to"] = new List<string> { packageIds[depIndex] }
                    });
                }
            }
        }

        // Build final document
        var document = new Dictionary<string, object>
        {
            ["@context"] = "https://spdx.org/rdf/3.0.1/spdx-context.jsonld",
            ["@graph"] = elements
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
}
