param(
    [string]$TestFilter = "Category=LiveM2LB",
    [switch]$NoBuild
)

# Runs the live M2LB Dev tests (network-dependent, never part of deterministic CI).
# The tests are skipped with an explicit reason unless RUN_LIVE_M2LB_TESTS=true; this script sets it for the run only.
# Only unauthenticated public discovery GETs against https://m2lbdev.bufetat.no/ are performed.

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\backend\BirkNext.Api.Tests\BirkNext.Api.Tests.csproj"
$buildArgs = @()
if ($NoBuild) { $buildArgs = @("--no-build") }

$discovery = & dotnet test $project -c Release @buildArgs --list-tests --filter $TestFilter 2>&1
$discovery | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$selected = @($discovery | Where-Object { $_ -match '^\s+BirkNext\.Api\.Tests\.' }).Count
Write-Host "Selected $selected live M2LB test(s)"
if ($selected -eq 0) {
    Write-Host "ERROR: Live M2LB gate selected 0 tests for filter '$TestFilter'." -ForegroundColor Red
    exit 2
}

$previous = $env:RUN_LIVE_M2LB_TESTS
try {
    $env:RUN_LIVE_M2LB_TESTS = "true"
    & dotnet test $project -c Release @buildArgs --filter $TestFilter
    exit $LASTEXITCODE
}
finally {
    $env:RUN_LIVE_M2LB_TESTS = $previous
}
