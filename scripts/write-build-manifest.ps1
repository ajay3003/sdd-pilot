<#
.SYNOPSIS
Write build-manifest.json into a BirkNext package from the run's resolved identity and the package's own compiled components, then verify
that the backend, the frontend and the bundled Browser Companion agree with it.

.DESCRIPTION
The identity is read from build-identity.json (scripts/resolve-build-identity.ps1); component versions are read from the compiled files in the
package, never copied. The manifest names the versioned archive but cannot contain its digest (see scripts/package-archive.ps1).
Exit code 0 = written and consistent; 1 = inconsistent (the package must not be published); 2 = script error.
#>
param(
    [Parameter(Mandatory = $true)][string]$IdentityFile,
    [Parameter(Mandatory = $true)][string]$PackageRoot
)
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'BirkNextBuildIdentity.psm1') -Force
    $identity = Read-BirkNextJson $IdentityFile
    if (-not $identity) { throw "Build identity not found or invalid: $IdentityFile" }
    $root = (Resolve-Path $PackageRoot).Path
    $manifest = New-BirkNextBuildManifest $identity $root
    $json = $manifest | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText((Join-Path $root 'build-manifest.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
    $problems = @(Test-BirkNextBuildProvenance $root)
    if ($problems.Count -gt 0) {
        foreach ($p in $problems) { Write-Host "[FAIL] $p" -ForegroundColor Red }
        exit 1
    }
    Write-Host "[OK] build-manifest.json: $($manifest.informationalVersion) ($($manifest.channel)); backend, frontend and Companion consistent" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "[ERROR] Build manifest could not be written: $_" -ForegroundColor Red
    exit 2
}
