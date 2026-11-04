<#
.SYNOPSIS
Validate EF Core migration file integrity in a source checkout.

.DESCRIPTION
Checks every EF Core migrations folder of the API project (Data/Migrations, Data/DeliveryReporting/Migrations,
Data/CanonicalInterpretation/Migrations, Data/SnapshotRegistry/Migrations, and any other "Migrations" folder). Fails when:
- A migration .cs file has no matching .Designer.cs file
- A .Designer.cs file has no matching migration .cs file
- A folder has no valid *ModelSnapshot.cs (with BuildModel)
- A Designer file's Migration attribute does not match its file name

This is a SOURCE check (developers, CI before publishing). A published tester package carries the migrations compiled into
BirkNext.Api.dll and is validated by scripts/validate-package.ps1 instead.

With -ManifestOut the migration ids per folder are written as JSON; packaging uses them to verify that the published assembly
really contains every migration.

This script is static and fast. It does not run tests, apply migrations, drop databases, or call dotnet ef database commands.
#>

param(
    [string]$ProjectPath = "",
    [string]$ManifestOut = ""
)

$ErrorActionPreference = "Stop"

function Write-Header {
    param([string]$Message)
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host $Message -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
}

function Write-Success { param([string]$Message) Write-Host "[OK] $Message" -ForegroundColor Green }
function Write-Failure { param([string]$Message) Write-Host "[ERROR] $Message" -ForegroundColor Red }

try {
    Write-Header "EF Core Migration Integrity Check (source)"

    if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $ProjectPath = Join-Path (Join-Path (Join-Path $repoRoot "AIAssisted") "backend") "BirkNext.Api"
    }

    $resolvedPath = (Resolve-Path $ProjectPath -ErrorAction Stop).Path
    $primary = Join-Path (Join-Path $resolvedPath "Data") "Migrations"
    if (-not (Test-Path $primary)) {
        Write-Failure "Migrations directory not found at: $primary"
        Write-Host "This check needs a source checkout. A published tester package is validated by scripts/validate-package.ps1." -ForegroundColor Yellow
        exit 1
    }

    # Every DbContext keeps its migrations under Data/ (never under bin/ or obj/, which can hold very deep build output paths).
    $folders = Get-ChildItem -Path (Join-Path $resolvedPath "Data") -Recurse -Directory -Filter "Migrations" | Sort-Object FullName

    $issueCount = 0
    $manifest = @()

    foreach ($folder in $folders) {
        $migrationsPath = $folder.FullName
        $relative = $migrationsPath.Substring($resolvedPath.Length).TrimStart('\', '/').Replace('\', '/')
        Write-Header "Migrations: $relative"

        $migrationFiles = @(Get-ChildItem -Path $migrationsPath -Filter "*.cs" |
            Where-Object { $_.Name -match "^[0-9]{14}_.+\.cs$" -and $_.Name -notmatch "Designer\.cs$" } |
            Select-Object -ExpandProperty Name)
        $designerFiles = @(Get-ChildItem -Path $migrationsPath -Filter "*.Designer.cs" |
            Where-Object { $_.Name -match "^[0-9]{14}_.+\.Designer\.cs$" } |
            Select-Object -ExpandProperty Name)
        Write-Host "Found $($migrationFiles.Count) migration files and $($designerFiles.Count) Designer files" -ForegroundColor Gray

        foreach ($file in $migrationFiles) {
            if (-not ($designerFiles -contains ($file -replace "\.cs$", ".Designer.cs"))) { Write-Failure "Missing Designer file for: $relative/$file"; $issueCount++ }
        }
        foreach ($file in $designerFiles) {
            if (-not ($migrationFiles -contains ($file -replace "\.Designer\.cs$", ".cs"))) { Write-Failure "Orphaned Designer file without matching migration: $relative/$file"; $issueCount++ }
            $migrationId = $file -replace "\.Designer\.cs$", ""
            $content = Get-Content (Join-Path $migrationsPath $file) -Raw
            if ($content -notmatch "Migration\(`"$([regex]::Escape($migrationId))`"\)") { Write-Failure "Designer file has missing or mismatched Migration attribute: $relative/$file"; $issueCount++ }
            else { $manifest += [PSCustomObject]@{ folder = $relative; id = $migrationId } }
        }

        # The model snapshot is the one non-timestamped *ModelSnapshot.cs (a migration may itself be named "…ModelSnapshot").
        $snapshots = @(Get-ChildItem -Path $migrationsPath -Filter "*ModelSnapshot.cs" | Where-Object { $_.Name -notmatch "^[0-9]{14}_" })
        if ($snapshots.Count -ne 1) { Write-Failure "Expected exactly one *ModelSnapshot.cs in $relative, found $($snapshots.Count)"; $issueCount++ }
        elseif ((Get-Content $snapshots[0].FullName -Raw) -notmatch "BuildModel") { Write-Failure "$($snapshots[0].Name) appears corrupted"; $issueCount++ }
        else { Write-Success "$($snapshots[0].Name) present; $($designerFiles.Count) migration(s) consistent" }
    }

    Write-Header "Summary"
    if ($issueCount -eq 0) {
        if ($ManifestOut) {
            $manifest | ConvertTo-Json -Depth 3 | Set-Content -Path $ManifestOut -Encoding UTF8
            Write-Host "Migration manifest written: $ManifestOut ($($manifest.Count) migrations)" -ForegroundColor Gray
        }
        Write-Success "All migration integrity checks passed ($($folders.Count) folders, $($manifest.Count) migrations)."
        exit 0
    }

    Write-Failure "Migration integrity check FAILED - $issueCount issue(s) found"
    Write-Host "Fix migrations before build, test, publish, or local startup." -ForegroundColor Yellow
    exit 1
}
catch {
    Write-Failure "Script error: $_"
    exit 1
}
