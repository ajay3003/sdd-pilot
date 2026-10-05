param(
    [string]$TestFilter = "Category=FrontendQualityPhase2ERealAcceptance"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\frontend\BirkNext.Web.PlaywrightTests\BirkNext.Web.PlaywrightTests.csproj"
$resultArgs = @()
if ($env:TEST_RESULTS_DIRECTORY) { New-Item -ItemType Directory -Force -Path $env:TEST_RESULTS_DIRECTORY | Out-Null; $resultArgs = @('--logger', 'trx', '--results-directory', $env:TEST_RESULTS_DIRECTORY) }

& dotnet build $project -c Release --no-restore --no-dependencies -p:RunPreStartedPlaywrightTests=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$discovery = & dotnet test $project -c Release --no-build -p:RunPreStartedPlaywrightTests=true --list-tests --filter $TestFilter 2>&1
$discovery | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$selected = @($discovery | Where-Object { $_ -match '^\s+BirkNext\.Web\.PlaywrightTests\.' }).Count
Write-Host "Selected $selected"
if ($selected -eq 0)
{
    Write-Host "ERROR: Phase 2E real aggregate gate selected 0 tests for filter '$TestFilter'." -ForegroundColor Red
    exit 2
}

& dotnet test $project -c Release --no-build -p:RunPreStartedPlaywrightTests=true --filter $TestFilter @resultArgs
exit $LASTEXITCODE
