param([string]$TestFilter = "Category=FrontendZapPassiveIntegration")
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\backend\BirkNext.Api.Tests\BirkNext.Api.Tests.csproj"
$discovery = & dotnet test $project -c Release --no-build --list-tests --filter $TestFilter 2>&1
$discovery | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$selected = @($discovery | Where-Object { $_ -match '^\s+BirkNext\.Api\.Tests\.' }).Count
Write-Host "Selected $selected"
if ($selected -eq 0) { Write-Host "ERROR: ZAP passive gate selected 0 tests for filter '$TestFilter'." -ForegroundColor Red; exit 2 }
& dotnet test $project -c Release --no-build --filter $TestFilter
exit $LASTEXITCODE
