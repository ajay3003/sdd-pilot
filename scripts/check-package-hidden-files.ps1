<#
.SYNOPSIS
Explicit allowlist for hidden files and local configuration in a staged BirkNext developer package (run before the CI artifact upload).

.DESCRIPTION
The GitHub workflow uploads the package with include-hidden-files: true, because the package needs AIAssisted/.env and the backend's
Playwright driver (.playwright/). That switch must not become a way to publish anything hidden. This check fails when the staged folder
contains:
  - a hidden file or folder (name starting with '.') that is not on the allowlist below;
  - an AIAssisted/.env that is not byte-identical to the checked-in AIAssisted/.env.example (so a workstation .env can never ride along);
  - workstation or secret material anywhere: appsettings.Local.json, appsettings.Development.json, user-secrets secrets.json, private keys
    or certificates (*.pfx, *.p12, *.key, *.pem), a .git folder.
No file content is printed; only relative paths.

.PARAMETER PackageRoot  The staged package folder (the one that is uploaded).
.PARAMETER EnvTemplate  The checked-in AIAssisted/.env.example the packaged .env must equal.
.PARAMETER PassThru     Return the problem list instead of exiting (tests).
#>
param(
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [Parameter(Mandatory = $true)][string]$EnvTemplate,
    [switch]$PassThru
)
$ErrorActionPreference = "Stop"

# Relative paths ('/' separators). A folder entry allows everything below it.
$allowedFiles = @('AIAssisted/.env', 'AIAssisted/.env.example', 'AIAssisted/frontend/.graphqlrc.json', 'AIAssisted/frontend/wwwroot/.gitkeep')
$allowedFolders = @('AIAssisted/backend/.playwright')
$forbiddenNames = @('appsettings.Local.json', 'appsettings.Development.json', 'secrets.json')
$forbiddenExtensions = @('.pfx', '.p12', '.key', '.pem')

$problems = New-Object System.Collections.Generic.List[string]
$root = (Resolve-Path -LiteralPath $PackageRoot).ProviderPath.TrimEnd('\', '/')
function Rel([string]$full) { $full.Substring($root.Length).TrimStart('\', '/').Replace('\', '/') }
function UnderAllowedFolder([string]$rel) { foreach ($f in $allowedFolders) { if ($rel -eq $f -or $rel.StartsWith("$f/")) { return $true } }; return $false }

foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
    $rel = Rel $item.FullName
    if (UnderAllowedFolder $rel) {
        if (-not $item.PSIsContainer -and ($forbiddenNames -contains $item.Name -or $forbiddenExtensions -contains $item.Extension.ToLowerInvariant())) { $problems.Add("not allowed in a package: $rel") }
        continue
    }
    if ($item.PSIsContainer -and $item.Name -eq '.git') { $problems.Add("repository metadata in the package: $rel"); continue }
    if (-not $item.PSIsContainer -and ($forbiddenNames -contains $item.Name -or $forbiddenExtensions -contains $item.Extension.ToLowerInvariant())) { $problems.Add("not allowed in a package: $rel"); continue }
    if ($item.Name.StartsWith('.')) {
        $ok = if ($item.PSIsContainer) { $allowedFolders | Where-Object { $_.StartsWith("$rel/") -or $_ -eq $rel } } else { $allowedFiles -contains $rel }
        if (-not $ok) { $problems.Add("hidden entry not on the allowlist: $rel") }
    }
}

$env = Join-Path $root 'AIAssisted/.env'
if (Test-Path -LiteralPath $env) {
    if (-not (Test-Path -LiteralPath $EnvTemplate)) { $problems.Add("the .env template was not found, so AIAssisted/.env cannot be checked") }
    else {
        $a = [IO.File]::ReadAllBytes($env); $b = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $EnvTemplate).ProviderPath)
        if ($a.Length -ne $b.Length -or [Convert]::ToBase64String($a) -ne [Convert]::ToBase64String($b)) { $problems.Add("AIAssisted/.env differs from the checked-in AIAssisted/.env.example (only the template's local defaults may be packaged)") }
    }
}

if ($PassThru) { return , $problems.ToArray() }
if ($problems.Count -gt 0) {
    Write-Host "Package hidden-file / local-configuration check FAILED:" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Yellow }
    exit 1
}
Write-Host "Package hidden-file / local-configuration check passed (allowlist only; AIAssisted/.env equals the checked-in template)." -ForegroundColor Green
exit 0
