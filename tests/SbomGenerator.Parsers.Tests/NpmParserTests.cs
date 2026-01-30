using Xunit;
using SbomGenerator.Parsers.Npm;
using SbomGenerator.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace SbomGenerator.Parsers.Tests;

public class NpmParserTests
{
    private readonly NpmParser _parser;

    public NpmParserTests()
    {
        var httpClientFactory = new MockHttpClientFactory();
        _parser = new NpmParser(httpClientFactory, NullLogger<NpmParser>.Instance);
    }

    [Fact]
    public void CanParse_PackageJson_ReturnsTrue()
    {
        Assert.True(_parser.CanParse("package.json"));
        Assert.True(_parser.CanParse("src/package.json"));
    }

    [Fact]
    public void CanParse_PackageLockJson_ReturnsTrue()
    {
        Assert.True(_parser.CanParse("package-lock.json"));
    }

    [Fact]
    public void CanParse_OtherFiles_ReturnsFalse()
    {
        Assert.False(_parser.CanParse("package.xml"));
        Assert.False(_parser.CanParse("packages.config"));
    }

    [Fact]
    public async Task ParseAsync_PackageJson_ParsesDependencies()
    {
        var content = """
        {
            "name": "test-project",
            "version": "1.0.0",
            "dependencies": {
                "lodash": "^4.17.21",
                "express": "~4.18.2"
            },
            "devDependencies": {
                "jest": "^29.0.0"
            }
        }
        """;

        var packages = await _parser.ParseAsync("package.json", content, "/repo");

        Assert.Equal(3, packages.Count);
        Assert.Contains(packages, p => p.Name == "lodash" && p.Version == "4.17.21");
        Assert.Contains(packages, p => p.Name == "express" && p.Version == "4.18.2");
        Assert.Contains(packages, p => p.Name == "jest" && p.Version == "29.0.0");
    }

    [Fact]
    public async Task ParseAsync_PackageLockV2_ParsesPackages()
    {
        var content = """
        {
            "name": "test-project",
            "lockfileVersion": 2,
            "packages": {
                "": {
                    "name": "test-project",
                    "version": "1.0.0"
                },
                "node_modules/lodash": {
                    "version": "4.17.21",
                    "resolved": "https://registry.npmjs.org/lodash/-/lodash-4.17.21.tgz",
                    "integrity": "sha512-abc123"
                }
            }
        }
        """;

        var packages = await _parser.ParseAsync("package-lock.json", content, "/repo");

        Assert.Single(packages);
        Assert.Equal("lodash", packages[0].Name);
        Assert.Equal("4.17.21", packages[0].Version);
        Assert.Equal("https://registry.npmjs.org/lodash/-/lodash-4.17.21.tgz", packages[0].DownloadUrl);
    }

    [Fact]
    public void Ecosystem_ReturnsNpm()
    {
        Assert.Equal(PackageEcosystem.Npm, _parser.Ecosystem);
    }
}

// Mock HttpClientFactory for testing
public class MockHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
