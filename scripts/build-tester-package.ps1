<#
.SYNOPSIS
Build the BirkNext Windows Tester Package (TesterNative runtime profile): self-contained backend and launcher, the frontend, the pinned native
PostgreSQL and k6, licenses, runtime/build/package manifests, and the versioned archive BirkNext-Tester-Windows-x64-<build version>.zip.

.DESCRIPTION
Inputs are pinned in AIAssisted/versioning/native-runtime.lock.json. Runtime archives are taken from -DownloadCache and verified against their
pinned SHA-256; with -AllowDownload a missing archive is downloaded from its pinned upstream URL into the cache first (and verified the same way).
Anything that does not match its pin stops the build: no 'latest', no substitute.

The build identity comes from -IdentityFile (scripts/resolve-build-identity.ps1 output); without it a Local identity is resolved into the
output folder. Nothing is installed on this PC and no pipeline is triggered.

Exit code 0 = package and archive written and validated; 1 = validation failed; 2 = script error / blocked input.
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$DownloadCache,
    [string]$IdentityFile = '',
    [switch]$AllowDownload,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = Split-Path -Parent $PSScriptRoot
$ai = Join-Path $repo 'AIAssisted'
function Sha256([string]$path) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }
function Step([string]$m) { Write-Host "[STEP] $m" -ForegroundColor Cyan }
function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

# A pinned input: the cached file must have the pinned SHA-256; downloaded only with -AllowDownload, from the pinned URL.
function Get-PinnedInput([string]$url, [string]$sha256, [string]$fileName) {
    New-Item -ItemType Directory -Force -Path $DownloadCache | Out-Null
    $file = Join-Path $DownloadCache $fileName
    if (-not (Test-Path -LiteralPath $file)) {
        if (-not $AllowDownload) { throw "BLOCKED: $fileName is not in $DownloadCache and -AllowDownload was not given (pinned source: $url)" }
        Write-Host "  downloading pinned $url"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
    }
    $actual = Sha256 $file
    if ($actual -ne $sha256) { throw "BLOCKED: $fileName has SHA-256 $actual, the lock pins $sha256. The file is not used." }
    Write-Host "  [OK] $fileName sha256 verified"
    return $file
}

function Test-Excluded([string]$entry, [string[]]$patterns) {
    foreach ($p in $patterns) {
        if ($p.EndsWith('/')) { if ($entry.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
        elseif ($entry -like $p) { return $true }
    }
    return $false
}

try {
    $lock = Get-Content (Join-Path $ai 'versioning/native-runtime.lock.json') -Raw | ConvertFrom-Json
    Import-Module (Join-Path $PSScriptRoot 'BirkNextBuildIdentity.psm1') -Force
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $OutDir = (Resolve-Path $OutDir).Path
    $stage = Join-Path $OutDir 'BirkNext-Tester'
    if (Test-Path -LiteralPath $stage) { throw "The staging folder already exists (never overwritten): $stage" }

    Step 'Build identity'
    if (-not $IdentityFile) {
        & (Join-Path $PSScriptRoot 'resolve-build-identity.ps1') -OutDir (Join-Path $OutDir 'identity') -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Build identity could not be resolved' }
        $IdentityFile = Join-Path $OutDir 'identity/build-identity.json'
    }
    $identity = Read-BirkNextJson $IdentityFile
    $props = [IO.Path]::ChangeExtension($IdentityFile, '.props')
    if (-not $identity -or -not (Test-Path $props)) { throw "Build identity not found: $IdentityFile (+ .props)" }

    Step 'Pinned runtime inputs'
    $pgZip = Get-PinnedInput $lock.postgresql.url $lock.postgresql.sha256 ([IO.Path]::GetFileName($lock.postgresql.url))
    $k6Zip = Get-PinnedInput $lock.k6.url $lock.k6.sha256 ([IO.Path]::GetFileName($lock.k6.url))
    $k6License = Get-PinnedInput $lock.k6.license.url $lock.k6.license.sha256 'k6-v1.0.0-LICENSE.md'

    Step 'Migration ids (source)'
    $migrations = Join-Path $OutDir 'migrations.json'
    & (Join-Path $PSScriptRoot 'check-migrations.ps1') -ManifestOut $migrations | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'check-migrations.ps1 failed' }

    Step 'Publish backend (self-contained win-x64)'
    Invoke-Checked 'dotnet' @('publish', (Join-Path $ai 'backend/BirkNext.Api/BirkNext.Api.csproj'), '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
        '-o', (Join-Path $stage 'AIAssisted/backend'), "-p:BirkNextBuildIdentityProps=$props", '-nologo', '-v', 'q')
    Step 'Publish frontend (WebAssembly)'
    Invoke-Checked 'dotnet' @('publish', (Join-Path $ai 'frontend/BirkNext.Web/BirkNext.Web.csproj'), '-c', $Configuration, '-o', (Join-Path $stage 'AIAssisted/frontend'),
        "-p:BirkNextBuildIdentityProps=$props", '-nologo', '-v', 'q')
    Step 'Publish launcher (self-contained single file)'
    $launcherOut = Join-Path $OutDir 'launcher-publish'
    Invoke-Checked 'dotnet' @('publish', (Join-Path $ai 'launcher/BirkNext.Launcher/BirkNext.Launcher.csproj'), '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-o', $launcherOut, "-p:BirkNextBuildIdentityProps=$props", '-nologo', '-v', 'q')
    Copy-Item (Join-Path $launcherOut 'Start-BirkNext.exe') (Join-Path $stage 'Start-BirkNext.exe')

    # Developer-only files never ship (the package is configured by the launcher, not by local settings files).
    foreach ($devOnly in @('appsettings.Development.json', 'appsettings.Local.json')) {
        Get-ChildItem -LiteralPath (Join-Path $stage 'AIAssisted/backend') -Filter $devOnly -File -ErrorAction SilentlyContinue | Remove-Item -Force
    }

    Step "Native PostgreSQL $($lock.postgresql.version)"
    $pgRoot = Join-Path $stage 'runtime/postgresql'
    $zip = [IO.Compression.ZipFile]::OpenRead($pgZip)
    $count = 0
    try {
        foreach ($e in $zip.Entries) {
            if ($e.FullName.EndsWith('/')) { continue }
            $included = $false
            foreach ($inc in $lock.postgresql.include) { if ($e.FullName.StartsWith($inc, [StringComparison]::OrdinalIgnoreCase)) { $included = $true } }
            if (-not $included -or (Test-Excluded $e.FullName $lock.postgresql.exclude)) { continue }
            $target = Join-Path $pgRoot ($e.FullName.Substring(6))
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $target, $true); $count++
        }
        $licenseEntry = $zip.GetEntry($lock.postgresql.license.archiveEntry)
        New-Item -ItemType Directory -Force -Path (Join-Path $stage 'licenses') | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($licenseEntry, (Join-Path $stage 'licenses/PostgreSQL-LICENSE.html'), $true)
    }
    finally { $zip.Dispose() }
    if ((Sha256 (Join-Path $stage 'licenses/PostgreSQL-LICENSE.html')) -ne $lock.postgresql.license.sha256) { throw 'The PostgreSQL license notice differs from the pinned one' }
    foreach ($tool in @('postgres', 'initdb', 'pg_ctl', 'pg_dump', 'pg_restore', 'pg_controldata', 'pg_isready', 'psql', 'pg_upgrade')) {
        if (-not (Test-Path (Join-Path $pgRoot "bin/$tool.exe"))) { throw "PostgreSQL tool $tool.exe is missing from the archive selection" }
    }
    Write-Host "  [OK] $count PostgreSQL files"

    Step "Native k6 $($lock.k6.version)"
    $zip = [IO.Compression.ZipFile]::OpenRead($k6Zip)
    try {
        New-Item -ItemType Directory -Force -Path (Join-Path $stage 'runtime/k6') | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($lock.k6.executableEntry), (Join-Path $stage 'runtime/k6/k6.exe'), $true)
    }
    finally { $zip.Dispose() }
    Copy-Item $k6License (Join-Path $stage 'licenses/k6-LICENSE.md')
    [IO.File]::WriteAllText((Join-Path $stage 'licenses/k6-SOURCE.txt'),
        "k6 $($lock.k6.version) is distributed unmodified under the GNU Affero General Public License v3 (licenses/k6-LICENSE.md).`r`nCorresponding source: $($lock.k6.license.sourceOffer)`r`nBinary: $($lock.k6.url)`r`n")

    Step 'Runtime manifest'
    $runtimeConfig = Get-Content (Join-Path $stage 'AIAssisted/backend/BirkNext.Api.runtimeconfig.json') -Raw | ConvertFrom-Json
    $dotnet = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' } | ForEach-Object { $_.version })[0]
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'config') | Out-Null
    $runtimeManifest = [ordered]@{
        format = 1
        distribution = 'WindowsTesterPackage'
        defaultRuntimeProfile = 'TesterNative'
        postgreSql = [ordered]@{ version = $lock.postgresql.version; major = $lock.postgresql.major; directory = 'runtime/postgresql'; sourceSha256 = $lock.postgresql.sha256 }
        k6 = [ordered]@{ version = $lock.k6.version; executable = 'runtime/k6/k6.exe'; sha256 = (Sha256 (Join-Path $stage 'runtime/k6/k6.exe')); sourceSha256 = $lock.k6.sha256; extensions = @() }
        dotNetRuntime = $dotnet
        targetOs = 'Windows 10 1809+ / Windows 11'
        architecture = 'x64'
        windowsPrerequisites = @($lock.postgresql.windowsPrerequisites)
    }
    [IO.File]::WriteAllText((Join-Path $stage 'config/runtime-manifest.json'), ($runtimeManifest | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))

    Step 'Notices and documentation'
    $notices = @"
BirkNext Windows Tester Package - third-party components

PostgreSQL $($lock.postgresql.version) (EnterpriseDB Windows x64 binaries)       PostgreSQL License        licenses/PostgreSQL-LICENSE.html
  bundled libraries in runtime/postgresql/bin: OpenSSL 3 (Apache-2.0), ICU 67 (Unicode-DFS/ICU), libxml2 (MIT), libxslt (MIT), zlib (Zlib),
  LZ4 (BSD-2-Clause), Zstandard (BSD-3-Clause), gettext libintl (LGPL-2.1-or-later), libiconv (LGPL-2.1-or-later), winpthreads (MIT/ZPL-2.1),
  libcurl (curl). Their license texts are not part of the upstream archive (see docs/deployment/birknext-windows-package-sbom.md).
k6 $($lock.k6.version) (Grafana Labs)                                             AGPL-3.0-only            licenses/k6-LICENSE.md, licenses/k6-SOURCE.txt
.NET $dotnet runtime (self-contained in AIAssisted/backend and Start-BirkNext.exe)  MIT                      https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
"@
    [IO.File]::WriteAllText((Join-Path $stage 'licenses/THIRD-PARTY-NOTICES.txt'), $notices, (New-Object Text.UTF8Encoding($false)))
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'docs') | Out-Null
    foreach ($doc in @('birknext-windows-tester-installation.md', 'birknext-windows-tester-upgrade-recovery.md', 'birknext-windows-package-sbom.md')) {
        $src = Join-Path $repo "docs/deployment/$doc"
        if (Test-Path $src) { Copy-Item $src (Join-Path $stage "docs/$doc") }
    }
    # One page for the tester: the normal path first, then what to do when something goes wrong.
    $readme = @(
        'BirkNext Tester (Windows)',
        '',
        'START',
        '  1. Extract the WHOLE zip to a normal local folder you can write to (for example C:\BirkNext). Do not run it from inside the zip.',
        '  2. Double-click Start-BirkNext.exe. Keep its window open while you use BirkNext.',
        '  3. The browser opens BirkNext and is connected automatically. Nothing to install, no code to copy.',
        '',
        'ANOTHER BROWSER OR WINDOW',
        '  Double-click Start-BirkNext.exe again: it opens a connected browser and shows the connection code.',
        '  Any other browser asks once for that code (also shown in the BirkNext window).',
        '',
        'STOP',
        '  Close the BirkNext window (or press Ctrl+C in it). Your data is kept.',
        '',
        'YOUR DATA',
        '  %LOCALAPPDATA%\BirkNext\TesterNative (outside this folder). A newer package uses the same data;',
        '  a verified backup is made automatically before a new version updates the database.',
        '',
        'IF SOMETHING GOES WRONG',
        '  The window says what happened and what to do. To send details to the BirkNext owner, run:',
        '    Start-BirkNext.exe --diagnostics   (writes a zip with logs and state; no passwords or codes)',
        '  Check the package and this PC without starting:  Start-BirkNext.exe --validate',
        '  Version:  the window title, or Start-BirkNext.exe --status',
        '  Azure Virtual Desktop / managed PCs: if Windows blocks Start-BirkNext.exe, ask IT to allow it (do not bypass security).',
        '',
        'More: docs\birknext-windows-tester-installation.md and docs\birknext-windows-tester-upgrade-recovery.md'
    ) -join "`r`n"
    [IO.File]::WriteAllText((Join-Path $stage 'README.txt'), $readme + "`r`n", (New-Object Text.UTF8Encoding($false)))

    Step 'Build manifest'
    $manifest = New-BirkNextBuildManifest $identity $stage
    $name = "BirkNext-Tester-Windows-x64-$($identity.buildVersion)"
    $manifest.artifactIdentity = [pscustomobject][ordered]@{ name = $name; archive = "$name.zip"; digest = "External: $name.zip.sha256 (an archive cannot contain its own digest)" }
    $manifest | Add-Member -NotePropertyName distribution -NotePropertyValue ([pscustomobject][ordered]@{
        kind = 'WindowsTesterPackage'; runtimeProfile = 'TesterNative'; architecture = 'x64'; postgreSql = $lock.postgresql.version; k6 = $lock.k6.version; dotNetRuntime = $dotnet })
    [IO.File]::WriteAllText((Join-Path $stage 'build-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    $problems = @(Test-BirkNextBuildProvenance $stage)
    if ($problems.Count -gt 0) { foreach ($p in $problems) { Write-Host "[FAIL] $p" -ForegroundColor Red }; exit 1 }

    Step 'Package manifest (every file + SHA-256 + migrations)'
    & (Join-Path $PSScriptRoot 'validate-package.ps1') -PackageRoot $stage -CreateManifest -MigrationManifest $migrations -SourceCommit $identity.sourceCommit
    if ($LASTEXITCODE -ne 0) { throw 'package-manifest.json could not be written' }

    Step 'Archive'
    & (Join-Path $PSScriptRoot 'package-archive.ps1') -PackageRoot $stage -OutDir (Join-Path $OutDir 'archive')
    if ($LASTEXITCODE -ne 0) { exit 1 }
    Write-Host "[OK] $name" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "[ERROR] $_" -ForegroundColor Red
    exit 2
}
