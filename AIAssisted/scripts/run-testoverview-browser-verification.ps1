param(
    # Where the isolated build goes. Never the API/Web projects' own bin/obj: a running BirkNext locks bin\Debug.
    [string]$ArtifactsPath = (Join-Path ([IO.Path]::GetTempPath()) "birknext-isolated-browser\art"),
    # xUnit filter; the default runs every isolated browser journey (Testoversikt B01-B24, P1-SCOPE, READY, CASE).
    [string]$Filter = "Category=IsolatedBrowser",
    # Folder for the TRX result and, on failure, screenshots and Playwright traces.
    [string]$ResultsPath = (Join-Path ([IO.Path]::GetTempPath()) "birknext-isolated-browser\results"),
    [switch]$NoBuild,
    [switch]$Headed
)

# Testoversikt isolated browser verification (V12, 2026-11-27).
#
# What is isolated, and where it is enforced (fail closed, in the test process itself - see
# backend/BirkNext.Api.Tests/BrowserVerification/IsolatedBrowserHost.cs):
#   - Database: the test assembly creates its own PostgreSQL database birknext_testrun_<hex> on the local server and refuses any other
#     (TestDatabaseIsolation + DatabaseConnection.TestDatabaseGuard). The developer's database is never opened; the run database is dropped at exit.
#   - App_Data: a temporary folder per test process (TestAppDataIsolation); logs go to the run's own folder.
#   - Ports: two free loopback ports, never 5000/5001/5173/5097; the API is reached through a loopback relay to the in-process host.
#   - Browser: Chromium with every request outside the two isolated origins aborted and reported.
#   - Before any browser step the host checks the EFFECTIVE runtime (database each context opened, App_Data root, launch-code file, ports,
#     hosting environment) and stops when one is not this run's own.
# Needs: the .NET SDK, the local PostgreSQL server (localhost:5432; its maintenance database only - nothing else is touched), Playwright Chromium
# in the user cache, NuGet packages in the local cache. No Docker/Podman start, no internet, no running BirkNext.
# Exit code: 0 = all selected journeys passed; 1 = a journey failed; 2 = prerequisite missing (nothing verified).

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$testProject = Join-Path $root "backend\BirkNext.Api.Tests\BirkNext.Api.Tests.csproj"

function Stop-Prerequisite([string]$message) { Write-Host "NOT VERIFIED (prerequisite): $message" -ForegroundColor Yellow; exit 2 }

# The artifacts must not be inside the API or Web project folders (their bin\Debug is what a running BirkNext uses).
$art = [IO.Path]::GetFullPath($ArtifactsPath)
foreach ($project in "backend\BirkNext.Api", "frontend\BirkNext.Web") {
    $dir = [IO.Path]::GetFullPath((Join-Path $root $project)).TrimEnd('\') + '\'
    if ($art.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase)) { Stop-Prerequisite "ArtifactsPath $art is inside $project; choose a separate folder." }
}

# PostgreSQL must answer locally; otherwise every host fails closed and a run would only measure that.
$pg = New-Object Net.Sockets.TcpClient
try { $pg.Connect("localhost", 5432) } catch { Stop-Prerequisite "no PostgreSQL server on localhost:5432 (the run creates its own temporary database there)." } finally { $pg.Dispose() }

if (-not $NoBuild) {
    & dotnet build $testProject --artifacts-path $art -nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }
}

$manifest = Join-Path $art "bin\BirkNext.Web\debug\BirkNext.Web.staticwebassets.runtime.json"
if (-not (Test-Path $manifest)) { Stop-Prerequisite "the built frontend manifest $manifest is missing (build without -NoBuild)." }

$names = "BIRKNEXT_ISOLATED_WEB_ROOT", "BIRKNEXT_ISOLATED_EVIDENCE_DIR", "BIRKNEXT_ISOLATED_HEADED"
$saved = @{}; foreach ($n in $names) { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
try {
    New-Item -ItemType Directory -Force $ResultsPath | Out-Null
    $env:BIRKNEXT_ISOLATED_WEB_ROOT = $manifest
    $env:BIRKNEXT_ISOLATED_EVIDENCE_DIR = Join-Path $ResultsPath "evidence"
    if ($Headed) { $env:BIRKNEXT_ISOLATED_HEADED = "1" }
    $trx = "testoverview-browser-$(Get-Date -Format yyyyMMdd-HHmmss).trx"
    & dotnet test $testProject --artifacts-path $art --no-build --filter $Filter --results-directory $ResultsPath `
        --logger "trx;LogFileName=$trx" --logger "console;verbosity=minimal"
    $code = $LASTEXITCODE
    Write-Host "Result: $(Join-Path $ResultsPath $trx)"
    if ($code -ne 0) { Write-Host "Failure evidence (screenshots, traces): $env:BIRKNEXT_ISOLATED_EVIDENCE_DIR" -ForegroundColor Red; exit 1 }
    exit 0
}
finally {
    foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
}
