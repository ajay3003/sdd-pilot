<#
.SYNOPSIS
Create the versioned BirkNext archive and its external SHA-256 checksum, or verify an archive against its checksum and its content.

.DESCRIPTION
Create: -PackageRoot <validated package folder> -OutDir <dir>. The archive is named from the package's build-manifest.json
(BirkNext-<build version>.zip); <name>.zip.sha256 (sha256sum format) is written next to it AFTER the archive is final and the archive is
never rewritten afterwards. A checksum proves the bytes are the ones the build produced; it is not a signature and does not prove who
published them.

Verify: -Verify -Archive <zip> [-ExtractTo <empty dir>]. Recomputes the SHA-256 and compares it with <zip>.sha256, extracts to an isolated
folder and runs validate-package.ps1 -RequireBuildManifest on the extracted copy (file hashes and build provenance).
Exit code 0 = OK; 1 = mismatch or invalid; 2 = script error.
#>
param(
    [string]$PackageRoot = '',
    [string]$OutDir = '',
    [switch]$Verify,
    [string]$Archive = '',
    [string]$ExtractTo = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Sha256([string]$path) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }
try {
    Import-Module (Join-Path $PSScriptRoot 'BirkNextBuildIdentity.psm1') -Force
    if ($Verify) {
        if (-not (Test-Path -LiteralPath $Archive)) { throw "Archive not found: $Archive" }
        $sumFile = "$Archive.sha256"
        if (-not (Test-Path -LiteralPath $sumFile)) { Write-Host "[FAIL] Checksum file missing: $sumFile" -ForegroundColor Red; exit 1 }
        $expected = ((Get-Content -LiteralPath $sumFile -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
        $actual = Sha256 $Archive
        if ($expected -ne $actual) { Write-Host "[FAIL] SHA-256 mismatch: expected $expected, archive is $actual" -ForegroundColor Red; exit 1 }
        Write-Host "[OK] SHA-256 matches $sumFile" -ForegroundColor Green
        if ($ExtractTo) {
            if ((Test-Path -LiteralPath $ExtractTo) -and (Get-ChildItem -LiteralPath $ExtractTo -Force | Select-Object -First 1)) { throw "Extraction folder is not empty: $ExtractTo" }
            New-Item -ItemType Directory -Force -Path $ExtractTo | Out-Null
            [System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $Archive).Path, (Resolve-Path $ExtractTo).Path)
            $validator = Join-Path $ExtractTo 'scripts/validate-package.ps1'
            if (-not (Test-Path $validator)) { Write-Host '[FAIL] The archive has no scripts/validate-package.ps1' -ForegroundColor Red; exit 1 }
            & (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File $validator -PackageRoot (Resolve-Path $ExtractTo).Path -RequireBuildManifest
            if ($LASTEXITCODE -ne 0) { exit 1 }
        }
        exit 0
    }
    $root = (Resolve-Path $PackageRoot).Path
    $manifest = Read-BirkNextJson (Join-Path $root 'build-manifest.json')
    if (-not $manifest) { Write-Host '[FAIL] build-manifest.json is missing: the archive name comes from it' -ForegroundColor Red; exit 1 }
    $name = $manifest.artifactIdentity.archive
    # BirkNext-<version>.zip (source-style package) or BirkNext-Tester-Windows-x64-<version>.zip (TesterNative package).
    if ($name -notmatch '^BirkNext-(Tester-Windows-x64-)?[0-9]+\.[0-9]+\.[0-9]+(-(ci\.[0-9]+|local|unknown))?\.zip$') { Write-Host "[FAIL] Unexpected archive name '$name'" -ForegroundColor Red; exit 1 }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $zip = Join-Path (Resolve-Path $OutDir).Path $name
    if (Test-Path -LiteralPath $zip) { throw "Archive already exists (never overwritten): $zip" }
    # Entry by entry with '/' separators (Windows PowerShell 5.1's CreateFromDirectory can write backslashes into entry names), files in a
    # fixed order; dotfiles such as AIAssisted/.env included.
    $files = Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object { $_.FullName.Substring($root.Length).Replace([char]92, [char]47) }
    $stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
    $complete = $false
    try {
        $zipArchive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($f in $files) {
                $entryName = $f.FullName.Substring($root.Length).Replace([char]92, [char]47).TrimStart([char]47)
                $entry = $zipArchive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $f.LastWriteTimeUtc
                # Long paths (deep package folders on Windows) are opened with the long-path prefix.
                $source = if ($env:OS -eq 'Windows_NT' -and $f.FullName.Length -ge 248) { "\\?\$($f.FullName)" } else { $f.FullName }
                $out = $entry.Open(); $in = [System.IO.File]::OpenRead($source)
                try { $in.CopyTo($out) } finally { $in.Dispose(); $out.Dispose() }
            }
        } finally { $zipArchive.Dispose() }
        $complete = $true
    } finally {
        $stream.Dispose()
        # A failed archive is removed: a partial file must never look like a finished one.
        if (-not $complete) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
    }
    $hash = Sha256 $zip
    [System.IO.File]::WriteAllText("$zip.sha256", "$hash  $name`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "[OK] $name  sha256 $hash" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "[ERROR] Archive step failed: $_" -ForegroundColor Red
    exit 2
}
