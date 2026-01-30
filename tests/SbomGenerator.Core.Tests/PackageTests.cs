using Xunit;
using SbomGenerator.Core.Models;

namespace SbomGenerator.Core.Tests;

public class PackageTests
{
    [Fact]
    public void GeneratePurl_Npm_ReturnsCorrectFormat()
    {
        var package = new Package
        {
            Name = "lodash",
            Version = "4.17.21",
            Ecosystem = PackageEcosystem.Npm
        };

        var purl = package.GeneratePurl();

        Assert.Equal("pkg:npm/lodash@4.17.21", purl);
    }

    [Fact]
    public void GeneratePurl_NuGet_ReturnsCorrectFormat()
    {
        var package = new Package
        {
            Name = "Newtonsoft.Json",
            Version = "13.0.3",
            Ecosystem = PackageEcosystem.NuGet
        };

        var purl = package.GeneratePurl();

        Assert.Equal("pkg:nuget/Newtonsoft.Json@13.0.3", purl);
    }

    [Fact]
    public void GeneratePurl_PyPI_ReturnsCorrectFormat()
    {
        var package = new Package
        {
            Name = "requests",
            Version = "2.31.0",
            Ecosystem = PackageEcosystem.PyPI
        };

        var purl = package.GeneratePurl();

        Assert.Equal("pkg:pypi/requests@2.31.0", purl);
    }

    [Fact]
    public void GeneratePurl_Maven_ReturnsCorrectFormat()
    {
        var package = new Package
        {
            Name = "org.apache.commons:commons-lang3",
            Version = "3.12.0",
            Ecosystem = PackageEcosystem.Maven
        };

        var purl = package.GeneratePurl();

        Assert.Equal("pkg:maven/org.apache.commons:commons-lang3@3.12.0", purl);
    }

    [Fact]
    public void GeneratePurl_Cargo_ReturnsCorrectFormat()
    {
        var package = new Package
        {
            Name = "serde",
            Version = "1.0.193",
            Ecosystem = PackageEcosystem.Cargo
        };

        var purl = package.GeneratePurl();

        Assert.Equal("pkg:cargo/serde@1.0.193", purl);
    }

    [Fact]
    public void GeneratePurl_Go_ReturnsCorrectFormat()
    {
        var package = new Package
        {
            Name = "github.com/gin-gonic/gin",
            Version = "v1.9.1",
            Ecosystem = PackageEcosystem.Go
        };

        var purl = package.GeneratePurl();

        Assert.Equal("pkg:golang/github.com/gin-gonic/gin@v1.9.1", purl);
    }

    [Fact]
    public void GeneratePurl_RubyGems_ReturnsCorrectFormat()
    {
        var package = new Package
        {
            Name = "rails",
            Version = "7.1.2",
            Ecosystem = PackageEcosystem.RubyGems
        };

        var purl = package.GeneratePurl();

        Assert.Equal("pkg:gem/rails@7.1.2", purl);
    }

    [Fact]
    public void Package_DefaultValues_AreCorrect()
    {
        var package = new Package
        {
            Name = "test",
            Version = "1.0.0",
            Ecosystem = PackageEcosystem.Npm
        };

        Assert.False(package.IsDirect);
        Assert.Empty(package.Files);
        Assert.Empty(package.Dependencies);
        Assert.Null(package.License);
        Assert.Null(package.Description);
        Assert.Null(package.Sha256);
    }
}
