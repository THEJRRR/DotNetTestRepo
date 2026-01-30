using System.Text.Json;
using System.Text.Json.Serialization;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Generators.CycloneDx;

/// <summary>
/// Generates CycloneDX 1.5+ format SBOMs.
/// </summary>
public class CycloneDxGenerator : ISbomGenerator
{
    public SbomFormat Format => SbomFormat.CycloneDx;

    public Task<string> GenerateAsync(
        RepositoryAnalysis analysis,
        SbomGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        var document = new CycloneDxDocument
        {
            BomFormat = "CycloneDX",
            SpecVersion = "1.5",
            SerialNumber = $"urn:uuid:{Guid.NewGuid()}",
            Version = 1,
            Metadata = new CdxMetadata
            {
                Timestamp = analysis.AnalyzedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Tools = new CdxTools
                {
                    Components = [
                        new CdxToolComponent
                        {
                            Type = "application",
                            Name = options.ToolName,
                            Version = options.ToolVersion
                        }
                    ]
                },
                Component = new CdxComponent
                {
                    Type = "application",
                    Name = GetProjectName(analysis.RepositoryUrl),
                    BomRef = "root-component"
                }
            },
            Components = analysis.Packages.Select((p, i) => CreateComponent(p, i)).ToList(),
            Dependencies = CreateDependencies(analysis.Packages)
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

    private static string GetProjectName(string repoUrl)
    {
        var uri = new Uri(repoUrl.TrimEnd('/'));
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : parts[^1];
    }

    private static CdxComponent CreateComponent(Package pkg, int index)
    {
        var component = new CdxComponent
        {
            Type = "library",
            BomRef = $"pkg-{index + 1}",
            Name = pkg.Name,
            Version = pkg.Version,
            Purl = pkg.Purl ?? pkg.GeneratePurl(),
            Description = pkg.Description
        };

        // Add licenses
        if (!string.IsNullOrEmpty(pkg.License))
        {
            component.Licenses = [
                new CdxLicenseChoice
                {
                    License = new CdxLicense
                    {
                        Id = pkg.License
                    }
                }
            ];
        }

        // Add hashes
        if (!string.IsNullOrEmpty(pkg.Sha256))
        {
            component.Hashes = [
                new CdxHash
                {
                    Alg = "SHA-256",
                    Content = pkg.Sha256
                }
            ];
        }

        // Add external references
        var externalRefs = new List<CdxExternalReference>();

        if (!string.IsNullOrEmpty(pkg.DownloadUrl))
        {
            externalRefs.Add(new CdxExternalReference
            {
                Type = "distribution",
                Url = pkg.DownloadUrl
            });
        }

        if (!string.IsNullOrEmpty(pkg.Homepage))
        {
            externalRefs.Add(new CdxExternalReference
            {
                Type = "website",
                Url = pkg.Homepage
            });
        }

        if (externalRefs.Count > 0)
        {
            component.ExternalReferences = externalRefs;
        }

        // Add author/supplier
        if (!string.IsNullOrEmpty(pkg.Author))
        {
            component.Author = pkg.Author;
        }

        return component;
    }

    private static List<CdxDependency> CreateDependencies(List<Package> packages)
    {
        var dependencies = new List<CdxDependency>();

        // Root component depends on direct dependencies
        var directDeps = packages
            .Where(p => p.IsDirect)
            .Select((_, i) => $"pkg-{packages.IndexOf(packages.First(p => p.IsDirect && packages.IndexOf(p) == packages.FindIndex(x => x == p))) + 1}")
            .ToList();

        // Create proper direct dependency refs
        var directPackageRefs = packages
            .Select((p, i) => new { Package = p, Index = i })
            .Where(x => x.Package.IsDirect)
            .Select(x => $"pkg-{x.Index + 1}")
            .ToList();

        dependencies.Add(new CdxDependency
        {
            Ref = "root-component",
            DependsOn = directPackageRefs
        });

        // Package dependencies
        for (var i = 0; i < packages.Count; i++)
        {
            var pkg = packages[i];
            var depRefs = new List<string>();

            foreach (var dep in pkg.Dependencies)
            {
                var depIndex = packages.FindIndex(p =>
                    p.Name == dep.Name &&
                    (p.Version == dep.ResolvedVersion || p.Version == dep.VersionRange));

                if (depIndex >= 0)
                {
                    depRefs.Add($"pkg-{depIndex + 1}");
                }
            }

            dependencies.Add(new CdxDependency
            {
                Ref = $"pkg-{i + 1}",
                DependsOn = depRefs.Count > 0 ? depRefs : null
            });
        }

        return dependencies;
    }
}

// CycloneDX 1.5 Document Model
public class CycloneDxDocument
{
    [JsonPropertyName("bomFormat")]
    public required string BomFormat { get; set; }

    [JsonPropertyName("specVersion")]
    public required string SpecVersion { get; set; }

    [JsonPropertyName("serialNumber")]
    public required string SerialNumber { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("metadata")]
    public CdxMetadata? Metadata { get; set; }

    [JsonPropertyName("components")]
    public List<CdxComponent>? Components { get; set; }

    [JsonPropertyName("dependencies")]
    public List<CdxDependency>? Dependencies { get; set; }
}

public class CdxMetadata
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("tools")]
    public CdxTools? Tools { get; set; }

    [JsonPropertyName("component")]
    public CdxComponent? Component { get; set; }
}

public class CdxTools
{
    [JsonPropertyName("components")]
    public List<CdxToolComponent>? Components { get; set; }
}

public class CdxToolComponent
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("version")]
    public required string Version { get; set; }
}

public class CdxComponent
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("bom-ref")]
    public required string BomRef { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("purl")]
    public string? Purl { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("licenses")]
    public List<CdxLicenseChoice>? Licenses { get; set; }

    [JsonPropertyName("hashes")]
    public List<CdxHash>? Hashes { get; set; }

    [JsonPropertyName("externalReferences")]
    public List<CdxExternalReference>? ExternalReferences { get; set; }
}

public class CdxLicenseChoice
{
    [JsonPropertyName("license")]
    public CdxLicense? License { get; set; }
}

public class CdxLicense
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class CdxHash
{
    [JsonPropertyName("alg")]
    public required string Alg { get; set; }

    [JsonPropertyName("content")]
    public required string Content { get; set; }
}

public class CdxExternalReference
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("url")]
    public required string Url { get; set; }
}

public class CdxDependency
{
    [JsonPropertyName("ref")]
    public required string Ref { get; set; }

    [JsonPropertyName("dependsOn")]
    public List<string>? DependsOn { get; set; }
}
