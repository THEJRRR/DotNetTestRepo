using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbomGenerator.Core.Interfaces;
using SbomGenerator.Core.Models;
using SbomGenerator.Core.Services;
using SbomGenerator.Generators.CycloneDx;
using SbomGenerator.Generators.Spdx22;
using SbomGenerator.Generators.Spdx30;
using SbomGenerator.Parsers.Npm;
using SbomGenerator.Parsers.NuGet;
using SbomGenerator.Parsers.PyPI;
using SbomGenerator.Parsers.Maven;
using SbomGenerator.Parsers.Cargo;
using SbomGenerator.Parsers.GoModules;
using SbomGenerator.Parsers.RubyGems;
using SbomGenerator.PackageExtractor.Services;

namespace SbomGenerator.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var repoOption = new Option<string>(
            aliases: ["--repo", "-r"],
            description: "GitHub repository URL")
        { IsRequired = true };

        var formatOption = new Option<string>(
            aliases: ["--format", "-f"],
            description: "Output format: spdx-2.2, spdx-3.0, or cyclonedx",
            getDefaultValue: () => "spdx-2.2");

        var outputOption = new Option<string>(
            aliases: ["--output", "-o"],
            description: "Output file path (required)")
        { IsRequired = true };

        var ecosystemsOption = new Option<string[]?>(
            aliases: ["--ecosystems", "-e"],
            description: "Filter to specific ecosystems (npm, nuget, pypi, maven, cargo, go, rubygems)");

        var noFilesOption = new Option<bool>(
            aliases: ["--no-files"],
            description: "Skip extracting file listings from packages",
            getDefaultValue: () => false);

        var noTransitiveOption = new Option<bool>(
            aliases: ["--no-transitive"],
            description: "Only include direct dependencies",
            getDefaultValue: () => false);

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose output",
            getDefaultValue: () => false);

        var rootCommand = new RootCommand("SBOM Generator - Generate Software Bill of Materials from GitHub repositories")
        {
            repoOption,
            formatOption,
            outputOption,
            ecosystemsOption,
            noFilesOption,
            noTransitiveOption,
            verboseOption
        };

        rootCommand.SetHandler(async (repo, format, output, ecosystems, noFiles, noTransitive, verbose) =>
        {
            await RunAsync(repo, format, output, ecosystems, noFiles, noTransitive, verbose);
        }, repoOption, formatOption, outputOption, ecosystemsOption, noFilesOption, noTransitiveOption, verboseOption);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task RunAsync(
        string repo,
        string format,
        string output,
        string[]? ecosystems,
        bool noFiles,
        bool noTransitive,
        bool verbose)
    {
        // Build service provider
        var services = new ServiceCollection();
        ConfigureServices(services, verbose);
        using var serviceProvider = services.BuildServiceProvider();

        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var orchestrator = serviceProvider.GetRequiredService<SbomOrchestrator>();

        try
        {
            // Parse format
            var sbomFormat = format.ToLowerInvariant() switch
            {
                "spdx-2.2" or "spdx22" => SbomFormat.Spdx22,
                "spdx-3.0" or "spdx30" => SbomFormat.Spdx30,
                "cyclonedx" or "cdx" => SbomFormat.CycloneDx,
                _ => throw new ArgumentException($"Unknown format: {format}")
            };

            // Parse ecosystems
            List<PackageEcosystem>? ecosystemFilter = null;
            if (ecosystems?.Length > 0)
            {
                ecosystemFilter = ecosystems.Select(e => e.ToLowerInvariant() switch
                {
                    "npm" => PackageEcosystem.Npm,
                    "nuget" => PackageEcosystem.NuGet,
                    "pypi" or "pip" => PackageEcosystem.PyPI,
                    "maven" or "gradle" => PackageEcosystem.Maven,
                    "cargo" or "rust" => PackageEcosystem.Cargo,
                    "go" or "golang" => PackageEcosystem.Go,
                    "rubygems" or "gem" or "ruby" => PackageEcosystem.RubyGems,
                    _ => throw new ArgumentException($"Unknown ecosystem: {e}")
                }).ToList();
            }

            var options = new SbomGenerationOptions
            {
                Format = sbomFormat,
                IncludeFiles = !noFiles,
                IncludeTransitive = !noTransitive,
                Ecosystems = ecosystemFilter
            };

            logger.LogInformation("Starting SBOM generation for {Repo}", repo);
            var sbom = await orchestrator.GenerateSbomAsync(repo, options);

            // Write to file
            await File.WriteAllTextAsync(output, sbom);
            logger.LogInformation("SBOM written to {Path}", output);

            logger.LogInformation("SBOM generation complete");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating SBOM");
            Environment.ExitCode = 1;
        }
    }

    static void ConfigureServices(ServiceCollection services, bool verbose)
    {
        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        });

        // Core services
        services.AddSingleton<IRepositoryCloner, GitRepositoryCloner>();
        services.AddSingleton<IManifestDetector, ManifestDetector>();
        services.AddSingleton<SbomOrchestrator>();

        // Parsers
        services.AddSingleton<IPackageParser, NpmParser>();
        services.AddSingleton<IPackageParser, NuGetParser>();
        services.AddSingleton<IPackageParser, PyPIParser>();
        services.AddSingleton<IPackageParser, MavenParser>();
        services.AddSingleton<IPackageParser, CargoParser>();
        services.AddSingleton<IPackageParser, GoModulesParser>();
        services.AddSingleton<IPackageParser, RubyGemsParser>();

        // Generators
        services.AddSingleton<ISbomGenerator, Spdx22Generator>();
        services.AddSingleton<ISbomGenerator, Spdx30Generator>();
        services.AddSingleton<ISbomGenerator, CycloneDxGenerator>();

        // Package extractors
        services.AddSingleton<IPackageFileExtractor, NpmPackageExtractor>();
        services.AddSingleton<IPackageFileExtractor, NuGetPackageExtractor>();
        services.AddSingleton<IPackageFileExtractor, PyPIPackageExtractor>();
        services.AddSingleton<IPackageFileExtractor, MavenPackageExtractor>();
        services.AddSingleton<IPackageFileExtractor, CargoPackageExtractor>();
        services.AddSingleton<IPackageFileExtractor, GoPackageExtractor>();
        services.AddSingleton<IPackageFileExtractor, RubyGemsPackageExtractor>();

        // HTTP client
        services.AddHttpClient();
    }
}
