using Xunit;
using SbomGenerator.Generators.Spdx22;
using SbomGenerator.Generators.Spdx30;
using SbomGenerator.Generators.CycloneDx;
using SbomGenerator.Core.Models;
using System.Text.Json;

namespace SbomGenerator.Generators.Tests;

public class SbomGeneratorTests
{
    private readonly RepositoryAnalysis _sampleAnalysis;
    private readonly SbomGenerationOptions _options;

    public SbomGeneratorTests()
    {
        _sampleAnalysis = new RepositoryAnalysis
        {
            RepositoryUrl = "https://github.com/test/repo",
            Packages =
            [
                new Package
                {
                    Name = "lodash",
                    Version = "4.17.21",
                    Ecosystem = PackageEcosystem.Npm,
                    IsDirect = true,
                    License = "MIT",
                    Description = "A modern JavaScript utility library"
                },
                new Package
                {
                    Name = "express",
                    Version = "4.18.2",
                    Ecosystem = PackageEcosystem.Npm,
                    IsDirect = true,
                    License = "MIT"
                }
            ]
        };

        _options = new SbomGenerationOptions
        {
            ToolName = "test-generator",
            ToolVersion = "1.0.0"
        };
    }

    [Fact]
    public async Task Spdx22Generator_GeneratesValidJson()
    {
        var generator = new Spdx22Generator();

        var result = await generator.GenerateAsync(_sampleAnalysis, _options);

        Assert.NotEmpty(result);
        
        var doc = JsonDocument.Parse(result);
        Assert.Equal("SPDX-2.3", doc.RootElement.GetProperty("spdxVersion").GetString());
        Assert.Equal("CC0-1.0", doc.RootElement.GetProperty("dataLicense").GetString());
        Assert.Equal("SPDXRef-DOCUMENT", doc.RootElement.GetProperty("SPDXID").GetString());
    }

    [Fact]
    public async Task Spdx22Generator_IncludesPackages()
    {
        var generator = new Spdx22Generator();

        var result = await generator.GenerateAsync(_sampleAnalysis, _options);

        var doc = JsonDocument.Parse(result);
        var packages = doc.RootElement.GetProperty("packages");
        Assert.Equal(2, packages.GetArrayLength());
    }

    [Fact]
    public async Task Spdx30Generator_GeneratesValidJsonLd()
    {
        var generator = new Spdx30Generator();

        var result = await generator.GenerateAsync(_sampleAnalysis, _options);

        Assert.NotEmpty(result);

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("@context", out _));
        Assert.True(doc.RootElement.TryGetProperty("@graph", out _));
    }

    [Fact]
    public async Task CycloneDxGenerator_GeneratesValidBom()
    {
        var generator = new CycloneDxGenerator();

        var result = await generator.GenerateAsync(_sampleAnalysis, _options);

        Assert.NotEmpty(result);

        var doc = JsonDocument.Parse(result);
        Assert.Equal("CycloneDX", doc.RootElement.GetProperty("bomFormat").GetString());
        Assert.Equal("1.5", doc.RootElement.GetProperty("specVersion").GetString());
    }

    [Fact]
    public async Task CycloneDxGenerator_IncludesComponents()
    {
        var generator = new CycloneDxGenerator();

        var result = await generator.GenerateAsync(_sampleAnalysis, _options);

        var doc = JsonDocument.Parse(result);
        var components = doc.RootElement.GetProperty("components");
        Assert.Equal(2, components.GetArrayLength());
    }

    [Fact]
    public async Task CycloneDxGenerator_IncludesPurl()
    {
        var generator = new CycloneDxGenerator();

        var result = await generator.GenerateAsync(_sampleAnalysis, _options);

        var doc = JsonDocument.Parse(result);
        var components = doc.RootElement.GetProperty("components");
        var firstComponent = components[0];
        
        Assert.True(firstComponent.TryGetProperty("purl", out var purl));
        Assert.StartsWith("pkg:npm/", purl.GetString());
    }
}
