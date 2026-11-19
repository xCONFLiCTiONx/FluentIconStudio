<#
.SYNOPSIS
    Build, publish, or run FluentIconStudio.

.DESCRIPTION
    A build automation script for FluentIconStudio supporting clean,
    rebuild, normal build, publish (framework-dependent or self-contained single-file),
    and execution.

.PARAMETER Configuration
    Build configuration: 'Release' (default) or 'Debug'.

.PARAMETER Clean
    Cleans previous build outputs and artifacts before running.

.PARAMETER Rebuild
    Performs a clean followed by a build.

.PARAMETER Publish
    Publishes the application for distribution.

.PARAMETER SelfContained
    When publishing, produces a self-contained single-file executable.

.PARAMETER Runtime
    The target runtime identifier when publishing (defaults to 'win-x64').

.PARAMETER OutputDirectory
    Directory to publish outputs to. Defaults to 'artifacts\publish'.

.PARAMETER Run
    Runs the compiled FluentIconStudio application after a successful build.

.PARAMETER NoRestore
    Skips implicit NuGet package restore during build.

.EXAMPLE
    .\.build.ps1
    Builds the solution in Release mode.

.EXAMPLE
    .\.build.ps1 -Configuration Debug
    Builds in Debug mode.

.EXAMPLE
    .\.build.ps1 -Publish -SelfContained
    Publishes a self-contained single-file executable to artifacts\publish.
#>

[CmdletBinding()]
param (
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Clean,
    [switch]$Rebuild,
    [switch]$Publish,
    [switch]$SelfContained,
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory,
    [switch]$Run,
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$solutionPath = Join-Path $repoRoot "FluentIconStudio.sln"
$projectPath = Join-Path $repoRoot "src\FluentIconStudio.csproj"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "[OK] $Message" -ForegroundColor Green
}

function Write-Failure {
    param([string]$Message)
    Write-Host "[ERROR] $Message" -ForegroundColor Red
}

# 1. Verify dotnet CLI availability
if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    Write-Failure "The 'dotnet' CLI was not found in PATH. Please install .NET SDK 10.0+."
    exit 1
}

$dotnetVersion = & dotnet --version
Write-Host "FluentIconStudio Build Script" -ForegroundColor Magenta
Write-Host "SDK Version   : $dotnetVersion" -ForegroundColor Gray
Write-Host "Configuration : $Configuration" -ForegroundColor Gray
Write-Host "Root Directory: $repoRoot" -ForegroundColor Gray

# 2. Handle Clean or Rebuild
if ($Clean -or $Rebuild) {
    Write-Step "Cleaning build artifacts and solution..."
    & dotnet clean "$solutionPath" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "Clean failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }

    $binObjDirs = Get-ChildItem -Path $repoRoot -Recurse -Directory -Include "bin", "obj" |
        Where-Object { $_.FullName -notmatch '\\(\.vs|\.git|node_modules)\\' }

    foreach ($dir in $binObjDirs) {
        if (Test-Path $dir.FullName) {
            try {
                Remove-Item -Path $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue
            } catch {
                # Ignore file locks if any process has files open
            }
        }
    }
    Write-Success "Clean completed."

    if ($Clean -and -not $Rebuild -and -not $Publish) {
        exit 0
    }
}

# 3. Build Solution
Write-Step "Building $Configuration..."
$buildArgs = @("build", "$solutionPath", "-c", $Configuration, "--nologo")
if ($NoRestore) {
    $buildArgs += "--no-restore"
}

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Failure "Build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}
Write-Success "Build succeeded."

# Deploy Release binaries to xPrograms
$deployDir = (Join-Path $env:LOCALAPPDATA "xPrograms\FluentIconStudio")
if ($Configuration -eq "Release") {
    $releaseBinDir = Join-Path $repoRoot "src\bin\Release\net10.0-windows"
    if (Test-Path (Join-Path $releaseBinDir "FluentIconStudio.exe")) {
        Write-Step "Deploying Release binaries to $deployDir..."
        Get-Process -Name "FluentIconStudio" -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 300
        New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
        Copy-Item -Path "$releaseBinDir\*" -Destination $deployDir -Recurse -Force
        Write-Success "Deployed to $deployDir"
    }
}

# 4. Handle Publish
if ($Publish) {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $repoRoot "artifacts\publish"
    }

    Write-Step "Publishing for $Runtime (SelfContained: $SelfContained)..."
    Write-Host "Output: $OutputDirectory" -ForegroundColor Gray

    $publishArgs = @(
        "publish", "$projectPath",
        "-c", $Configuration,
        "-r", $Runtime,
        "-o", "$OutputDirectory",
        "--nologo"
    )

    if ($SelfContained) {
        $publishArgs += @(
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true"
        )
    } else {
        $publishArgs += @("--self-contained", "false")
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "Publish failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
    Write-Success "Publish completed successfully to $OutputDirectory"
}

# 5. Handle Run
if ($Run) {
    $targetExe = Join-Path $repoRoot "src\bin\$Configuration\net10.0-windows\FluentIconStudio.exe"
    if (-not (Test-Path $targetExe)) {
        if ($Publish -and (Test-Path (Join-Path $OutputDirectory "FluentIconStudio.exe"))) {
            $targetExe = Join-Path $OutputDirectory "FluentIconStudio.exe"
        }
    }

    if (Test-Path $targetExe) {
        Write-Step "Launching $targetExe..."
        Start-Process -FilePath $targetExe
    } else {
        Write-Failure "Could not locate compiled executable at $targetExe."
        exit 1
    }
}

Write-Success "All tasks completed successfully."
exit 0

