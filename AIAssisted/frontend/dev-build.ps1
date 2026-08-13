# Development build wrapper for Task Explorer scoped CSS
# This script builds the project and automatically sets up the scoped CSS for the dev server
#
# Usage:
#   .\dev-build.ps1                              # Build and setup CSS (default: Debug)
#   .\dev-build.ps1 -Configuration Release       # Build Release config
#   .\dev-build.ps1 -Test                        # Build, setup CSS, skip CSS for tests
#   .\dev-build.ps1 -Clean                       # Clean and reset

param(
    [string]$Configuration = "Debug",
    [switch]$Test,
    [switch]$Clean
)

$BirkNextWebPath = "$PSScriptRoot/BirkNext.Web"
$setupScript = "$PSScriptRoot/setup-dev-scoped-css.ps1"

function Cleanup-CSS {
    & $setupScript -Clean
}

# Clean if requested
if ($Clean) {
    Write-Host "Cleaning previous build artifacts..." -ForegroundColor Yellow
    dotnet clean BirkNext.Web -c $Configuration
    Cleanup-CSS
}

# Build the project
Write-Host ""
Write-Host "Building BirkNext.Web ($Configuration)..." -ForegroundColor Cyan
dotnet build BirkNext.Web -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Setup scoped CSS for Debug builds (needed for dev server)
if ($Configuration -eq "Debug" -and -not $Test) {
    Write-Host ""
    Write-Host "Setting up scoped CSS for development..." -ForegroundColor Cyan
    & $setupScript
    if ($LASTEXITCODE -ne 0) {
        Write-Host "CSS setup failed!" -ForegroundColor Red
        exit 1
    }
}

# Run tests if requested
if ($Test) {
    Write-Host ""
    # Clean up CSS before tests (it interferes with test builds)
    if (Test-Path "$BirkNextWebPath/wwwroot/BirkNext.Web.styles.css") {
        Write-Host "Preparing for tests (removing development CSS)..." -ForegroundColor Yellow
        Cleanup-CSS
    }

    Write-Host "Running tests..." -ForegroundColor Cyan
    dotnet test BirkNext.Web.Tests
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed!" -ForegroundColor Red
        exit 1
    }

    Write-Host ""
    Write-Host "Tests passed! Restoring CSS for development..." -ForegroundColor Green
    & $setupScript
}

Write-Host ""
Write-Host "Done! Project is ready for development." -ForegroundColor Green
