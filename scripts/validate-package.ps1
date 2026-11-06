<#
.SYNOPSIS
Validate a BirkNext tester package (the published, runnable artifact) against what scripts/start-local.ps1 needs.

.DESCRIPTION
A tester package is published output, not a source checkout: EF Core migrations are compiled into BirkNext.Api.dll, so there is no
AIAssisted/backend/BirkNext.Api/Data/Migrations folder and none is required. This script checks, without starting anything:
- launcher and scripts (start-local.ps1/.bat, this script)
- backend runtime (BirkNext.Api.dll, .deps.json, .runtimeconfig.json, appsettings.json) and no developer-only settings files
- frontend runtime (wwwroot/index.html and a non-empty wwwroot/_framework)
- local configuration templates (AIAssisted/docker-compose.yml, AIAssisted/.env)
- package-manifest.json: every file the build packaged is still present (static, data and schema assets included)
- every EF Core migration the source had is embedded in BirkNext.Api.dll (by its Migration id)
- the package is a published package (no .csproj source projects that would switch the launcher to source mode)

Build time: -CreateManifest -MigrationManifest <check-migrations.ps1 -ManifestOut file> writes package-manifest.json.

Exit code 0 = complete; 1 = incomplete (tester-facing message); 2 = script error.
#>

param(
    [string]$PackageRoot = "",
    [switch]$CreateManifest,
    [string]$MigrationManifest = "",
    [string]$SourceCommit = ""
)

$ErrorActionPreference = "Stop"
$TesterAdvice = "Download a new tester artifact or contact the build owner."

function P([string]$root, [string[]]$parts) { $path = $root; foreach ($p in $parts) { $path = Join-Path $path $p }; return $path }

try {
    if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = Split-Path -Parent $PSScriptRoot }
    $root = (Resolve-Path $PackageRoot -ErrorAction Stop).Path
    $backend = P $root @("AIAssisted", "backend")
    $frontend = P $root @("AIAssisted", "frontend", "wwwroot")
    $apiDll = Join-Path $backend "BirkNext.Api.dll"
    $manifestPath = Join-Path $root "package-manifest.json"

    if ($CreateManifest) {
        if (-not (Test-Path $MigrationManifest)) { throw "Migration manifest not found: $MigrationManifest (run scripts/check-migrations.ps1 -ManifestOut first)" }
        # Windows PowerShell 5.1 emits a JSON array as ONE object: enumerate it explicitly.
        $migrations = @((Get-Content $MigrationManifest -Raw | ConvertFrom-Json) | ForEach-Object { $_ })
        # -Force: dotfiles such as AIAssisted/.env are hidden on Linux and macOS and must be listed and checked too.
        $files = Get-ChildItem -Path $root -Recurse -File -Force |
            ForEach-Object { $_.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/') } |
            Where-Object { $_ -ne "package-manifest.json" } | Sort-Object
        [PSCustomObject]@{
            format = 1
            createdAt = (Get-Date).ToUniversalTime().ToString("o")
            sourceCommit = $SourceCommit
            migrations = $migrations
            files = @($files)
        } | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestPath -Encoding UTF8
        Write-Host "[OK] package-manifest.json written: $($files.Count) files, $($migrations.Count) migrations" -ForegroundColor Green
        exit 0
    }

    $problems = New-Object System.Collections.Generic.List[string]
    $migrationProblem = $false
    $pathProblem = $false
    $onWindows = $env:OS -eq "Windows_NT"
    # A file beyond the Windows 260-character path limit "does not exist" for Windows PowerShell and many tools; look it up with the
    # long-path prefix so a deep extraction folder is reported as such, not as a broken package.
    function Exists([string]$full) {
        if (Test-Path -LiteralPath $full) { return $true }
        if ($onWindows) { return [System.IO.File]::Exists("\\?\$full") -or [System.IO.Directory]::Exists("\\?\$full") }
        return $false
    }
    function Need([string]$relative, [string]$what) {
        $full = P $root ($relative -split '/')
        if (-not (Test-Path -LiteralPath $full)) { $problems.Add("$what is missing ($relative)") }
        elseif ((Get-Item -LiteralPath $full -Force) -is [System.IO.FileInfo] -and (Get-Item -LiteralPath $full -Force).Length -eq 0) { $problems.Add("$what is empty ($relative)") }
    }

    Need "scripts/start-local.ps1" "Launcher script"
    Need "scripts/start-local.bat" "Launcher shortcut"
    Need "scripts/BirkNextPreflight.psm1" "Launcher preflight module"
    Need "AIAssisted/backend/BirkNext.Api.dll" "Backend runtime"
    Need "AIAssisted/backend/BirkNext.Api.deps.json" "Backend dependency manifest"
    Need "AIAssisted/backend/BirkNext.Api.runtimeconfig.json" "Backend runtime configuration"
    Need "AIAssisted/backend/appsettings.json" "Backend base configuration"
    Need "AIAssisted/frontend/wwwroot/index.html" "Frontend entry page"
    Need "AIAssisted/docker-compose.yml" "Local database compose template"
    Need "AIAssisted/.env" "Local environment defaults"
    $framework = Join-Path $frontend "_framework"
    if (-not (Test-Path $framework) -or -not (Get-ChildItem -Path $framework -File -Force -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        $problems.Add("Frontend runtime files are missing (AIAssisted/frontend/wwwroot/_framework)")
    }
    foreach ($devOnly in @("appsettings.Development.json", "appsettings.Local.json")) {
        if (Test-Path (Join-Path $backend $devOnly)) { $problems.Add("Developer-only settings must not be packaged (AIAssisted/backend/$devOnly)") }
    }
    if (Get-ChildItem -Path (Join-Path $root "AIAssisted") -Recurse -Force -Filter "*.csproj" -ErrorAction SilentlyContinue | Select-Object -First 1) {
        $problems.Add("Source project files (.csproj) are present: this is not a published tester package")
    }

    if (-not (Test-Path $manifestPath)) {
        $problems.Add("package-manifest.json is missing (the package was not produced by the BirkNext pipeline)")
    }
    else {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $longest = (@($manifest.files) | ForEach-Object { $_.Length } | Measure-Object -Maximum).Maximum
        if ($onWindows -and ($root.Length + 1 + $longest) -ge 260) {
            $pathProblem = $true
            $problems.Add("The package folder path is too long for Windows ($($root.Length + 1 + $longest) characters for the deepest file; the limit is 259). Extract the package to a shorter folder, for example C:\BirkNext.")
        }
        $missing = @($manifest.files | Where-Object { -not (Exists (P $root ($_ -split '/'))) })
        if ($missing.Count -gt 0) {
            $problems.Add("$($missing.Count) packaged file(s) are missing, e.g. $(($missing | Select-Object -First 3) -join ', ')")
        }
        if (Test-Path $apiDll) {
            # Each EF Core migration carries [Migration("<id>")] in the compiled assembly: its id must be present.
            $text = [System.Text.Encoding]::GetEncoding(28591).GetString([System.IO.File]::ReadAllBytes($apiDll))
            $absent = @($manifest.migrations | Where-Object { -not $text.Contains($_.id) })
            if ($absent.Count -gt 0) {
                $migrationProblem = $true
                $problems.Add("$($absent.Count) EF Core migration(s) are not in BirkNext.Api.dll, e.g. $(($absent | Select-Object -First 2 | ForEach-Object { $_.id }) -join ', ')")
            }
            elseif (@($manifest.migrations).Count -eq 0) {
                $migrationProblem = $true
                $problems.Add("The package manifest lists no EF Core migrations")
            }
        }
    }

    if ($problems.Count -eq 0) {
        Write-Host "[OK] BirkNext tester package is complete ($(@($manifest.files).Count) files, $(@($manifest.migrations).Count) EF Core migrations embedded)." -ForegroundColor Green
        exit 0
    }

    Write-Host ""
    if ($pathProblem -and $problems.Count -eq 1) {
        Write-Host "BirkNext cannot start from this folder: the extracted package path is too long for Windows." -ForegroundColor Red
    }
    elseif ($migrationProblem) {
        Write-Host "BirkNext tester package is incomplete. Required EF Core migration files are missing. $TesterAdvice" -ForegroundColor Red
    }
    else {
        Write-Host "BirkNext tester package is incomplete. $TesterAdvice" -ForegroundColor Red
    }
    foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Yellow }
    exit 1
}
catch {
    Write-Host "[ERROR] Package validation could not run: $_" -ForegroundColor Red
    exit 2
}
