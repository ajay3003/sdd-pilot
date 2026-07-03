<#
.SYNOPSIS
Compatibility wrapper for the canonical migration integrity checker.

.DESCRIPTION
The canonical implementation lives at ../../scripts/check-migrations.ps1.
#>

param(
    [string]$ProjectPath = "."
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$canonicalScript = Join-Path $repoRoot "scripts\check-migrations.ps1"

if (-not (Test-Path $canonicalScript)) {
    Write-Error "Migration integrity check script not found: $canonicalScript"
    exit 1
}

if ($ProjectPath -eq ".") {
    $ProjectPath = Join-Path $PSScriptRoot "BirkNext.Api"
}

& $canonicalScript -ProjectPath $ProjectPath
exit $LASTEXITCODE
