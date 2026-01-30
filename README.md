# SBOM Generator CLI

A command-line tool for generating Software Bill of Materials (SBOMs) from public GitHub repositories. Analyzes package manifest files, resolves dependencies, extracts file listings, and generates SBOMs in SPDX 2.2.3, SPDX 3.0.1, or CycloneDX formats.

## Features

- **Multi-Ecosystem Support**: NPM, NuGet, PyPI, Maven/Gradle, Cargo, Go Modules, RubyGems
- **Multiple SBOM Formats**: SPDX 2.2.3, SPDX 3.0.1, CycloneDX 1.5
- **Transitive Dependencies**: Automatically resolves and includes transitive dependencies
- **File Listings**: Extracts file listings from packages with SHA256 hashes
- **PURL Support**: Generates Package URLs for all discovered packages

## Installation

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Git](https://git-scm.com/downloads)

### Build from Source
```bash
git clone https://github.com/your-org/sbom-generator.git
cd sbom-generator
dotnet build
```

### Run
```bash
dotnet run --project src/SbomGenerator.Cli -- --help
```

## Usage

### Basic Usage
```bash
# Generate SPDX 2.2 SBOM (default)
sbom-generator --repo https://github.com/expressjs/express

# Generate CycloneDX SBOM
sbom-generator --repo https://github.com/expressjs/express --format cyclonedx

# Generate SPDX 3.0 SBOM and save to file
sbom-generator --repo https://github.com/expressjs/express --format spdx-3.0 --output sbom.json
```

### Command Line Options

| Option | Alias | Description | Default |
|--------|-------|-------------|---------|
| `--repo` | `-r` | GitHub repository URL (required) | - |
| `--format` | `-f` | Output format: `spdx-2.2`, `spdx-3.0`, `cyclonedx` | `spdx-2.2` |
| `--output` | `-o` | Output file path | Console |
| `--ecosystems` | `-e` | Filter to specific ecosystems | All |
| `--no-files` | - | Skip extracting file listings | `false` |
| `--no-transitive` | - | Only include direct dependencies | `false` |
| `--verbose` | `-v` | Enable verbose output | `false` |

### Examples

```bash
# Only analyze NPM and NuGet packages
sbom-generator -r https://github.com/dotnet/aspnetcore -e npm -e nuget

# Generate without file listings (faster)
sbom-generator -r https://github.com/facebook/react --no-files

# Direct dependencies only
sbom-generator -r https://github.com/pallets/flask --no-transitive

# Verbose output for debugging
sbom-generator -r https://github.com/rust-lang/rust -v
```

## Supported Package Ecosystems

| Ecosystem | Manifest Files |
|-----------|---------------|
| **NPM** | `package.json`, `package-lock.json` |
| **NuGet** | `*.csproj`, `*.fsproj`, `*.vbproj`, `packages.config`, `Directory.Packages.props` |
| **PyPI** | `requirements.txt`, `pyproject.toml`, `setup.py`, `Pipfile`, `Pipfile.lock` |
| **Maven** | `pom.xml`, `build.gradle`, `build.gradle.kts` |
| **Cargo** | `Cargo.toml`, `Cargo.lock` |
| **Go** | `go.mod`, `go.sum` |
| **RubyGems** | `Gemfile`, `Gemfile.lock`, `*.gemspec` |

## Output Formats

### SPDX 2.2.3 (Default)
Industry-standard format for software bill of materials. JSON output conforming to SPDX 2.3 specification.

### SPDX 3.0.1
Latest SPDX specification using JSON-LD format with improved semantics and structure.

### CycloneDX 1.5
OWASP standard for security-focused SBOMs. Widely supported by security scanning tools.

## Architecture

```
src/
├── SbomGenerator.Cli/           # CLI entry point
├── SbomGenerator.Core/          # Core models and interfaces
├── SbomGenerator.Parsers/       # Package ecosystem parsers
├── SbomGenerator.Generators/    # SBOM format generators
└── SbomGenerator.PackageExtractor/  # Package file extraction
```

## Development

### Building
```bash
dotnet build
```

### Running Tests
```bash
dotnet test
```

### Creating a Release Build
```bash
dotnet publish src/SbomGenerator.Cli -c Release -o ./publish
```

## License

MIT License - see [LICENSE](LICENSE) for details.
