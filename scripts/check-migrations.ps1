<#
.SYNOPSIS
Validate EF Core migration file integrity.

.DESCRIPTION
Fails when timestamped EF migration files are structurally incomplete, including:
- A migration .cs file without its matching .Designer.cs file
- A .Designer.cs file without its matching migration .cs file
- A missing or invalid AppDbContextModelSnapshot.cs
- A mismatched Migration attribute in a Designer file

This script is intentionally static and fast. It does not run tests, apply
migrations, drop databases, or call dotnet ef database commands.
#>

param(
    [string]$ProjectPath = ""
)

$ErrorActionPreference = "Stop"

function Write-Header {
    param([string]$Message)
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host $Message -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "[OK] $Message" -ForegroundColor Green
}

function Write-WarningLine {
    param([string]$Message)
    Write-Host "[WARN] $Message" -ForegroundColor Yellow
}

function Write-Failure {
    param([string]$Message)
    Write-Host "[ERROR] $Message" -ForegroundColor Red
}

try {
    Write-Header "EF Core Migration Integrity Check"

    if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $ProjectPath = Join-Path $repoRoot "AIAssisted/backend/BirkNext.Api"
    }

    $resolvedPath = Resolve-Path $ProjectPath -ErrorAction Stop
    $migrationsPath = Join-Path $resolvedPath "Data/Migrations"

    if (-not (Test-Path $migrationsPath)) {
        Write-Failure "Migrations directory not found at: $migrationsPath"
        exit 1
    }

    Write-Host "Migrations directory: $migrationsPath" -ForegroundColor Gray

    $migrationFiles = Get-ChildItem -Path $migrationsPath -Filter "*.cs" |
        Where-Object { $_.Name -match "^[0-9]{14}_.+\.cs$" -and $_.Name -notmatch "Designer\.cs$" } |
        Select-Object -ExpandProperty Name

    $designerFiles = Get-ChildItem -Path $migrationsPath -Filter "*.Designer.cs" |
        Where-Object { $_.Name -match "^[0-9]{14}_.+\.Designer\.cs$" } |
        Select-Object -ExpandProperty Name

    $snapshotFile = Join-Path $migrationsPath "AppDbContextModelSnapshot.cs"

    Write-Host ""
    Write-Host "Found $($migrationFiles.Count) migration files" -ForegroundColor Gray
    Write-Host "Found $($designerFiles.Count) Designer files" -ForegroundColor Gray

    $allValid = $true
    $issueCount = 0

    Write-Header "Check 1: Migration Files Complete"
    foreach ($file in $migrationFiles) {
        $designerName = $file -replace "\.cs$", ".Designer.cs"
        if (-not ($designerFiles -contains $designerName)) {
            Write-Failure "Missing Designer file for: $file"
            $allValid = $false
            $issueCount++
        }
    }

    if ($allValid) {
        Write-Success "All migration files have matching Designer files"
    }

    Write-Header "Check 2: Designer Files Valid"
    foreach ($file in $designerFiles) {
        $migrationName = $file -replace "\.Designer\.cs$", ".cs"
        if (-not ($migrationFiles -contains $migrationName)) {
            Write-Failure "Orphaned Designer file without matching migration: $file"
            $allValid = $false
            $issueCount++
        }
    }

    if ($allValid) {
        Write-Success "No orphaned Designer files"
    }

    Write-Header "Check 3: Model Snapshot"
    if (Test-Path $snapshotFile) {
        $snapshotContent = Get-Content $snapshotFile -Raw
        if ($snapshotContent -match "AppDbContextModelSnapshot" -and $snapshotContent -match "BuildModel") {
            Write-Success "AppDbContextModelSnapshot.cs is present and valid"
        }
        else {
            Write-Failure "AppDbContextModelSnapshot.cs appears corrupted"
            $allValid = $false
            $issueCount++
        }
    }
    else {
        Write-Failure "AppDbContextModelSnapshot.cs not found"
        $allValid = $false
        $issueCount++
    }

    Write-Header "Check 4: Migration Attributes"
    foreach ($file in $designerFiles) {
        $migrationId = $file -replace "\.Designer\.cs$", ""
        $designerPath = Join-Path $migrationsPath $file
        $designerContent = Get-Content $designerPath -Raw

        if ($designerContent -notmatch "Migration\(`"$([regex]::Escape($migrationId))`"\)") {
            Write-Failure "Designer file has missing or mismatched Migration attribute: $file"
            $allValid = $false
            $issueCount++
        }
    }

    if ($allValid) {
        Write-Success "Migration attributes match Designer filenames"
    }

    Write-Header "Summary"
    if ($allValid) {
        Write-Success "All migration integrity checks passed!"
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
