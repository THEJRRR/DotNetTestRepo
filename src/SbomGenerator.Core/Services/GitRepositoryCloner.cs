using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SbomGenerator.Core.Interfaces;

namespace SbomGenerator.Core.Services;

/// <summary>
/// Clones Git repositories using the git CLI.
/// </summary>
public class GitRepositoryCloner : IRepositoryCloner
{
    private readonly ILogger<GitRepositoryCloner> _logger;

    public GitRepositoryCloner(ILogger<GitRepositoryCloner> logger)
    {
        _logger = logger;
    }

    public async Task<string> CloneAsync(
        string repositoryUrl,
        string? branch = null,
        CancellationToken cancellationToken = default)
    {
        // Create temp directory
        var tempPath = Path.Combine(Path.GetTempPath(), "sbom-generator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        try
        {
            var args = new List<string> { "clone", "--depth", "1" };

            if (!string.IsNullOrEmpty(branch))
            {
                args.AddRange(["--branch", branch]);
            }

            args.Add(repositoryUrl);
            args.Add(tempPath);

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _logger.LogDebug("Running: git {Args}", string.Join(" ", args));

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process");

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException($"Git clone failed: {error}");
            }

            _logger.LogDebug("Repository cloned to {Path}", tempPath);
            return tempPath;
        }
        catch
        {
            // Cleanup on failure
            Cleanup(tempPath);
            throw;
        }
    }

    public void Cleanup(string repositoryPath)
    {
        try
        {
            if (Directory.Exists(repositoryPath))
            {
                // Remove read-only attributes from .git files
                RemoveReadOnlyAttributes(repositoryPath);
                Directory.Delete(repositoryPath, recursive: true);
                _logger.LogDebug("Cleaned up {Path}", repositoryPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup {Path}", repositoryPath);
        }
    }

    private static void RemoveReadOnlyAttributes(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
        }
    }
}
