<#
.SYNOPSIS
    Sets the backend settings for a local, unattended Skolenærvær TT02 run in THIS PowerShell session only.

.DESCRIPTION
    Developer-only helper. It sets process-scope environment variables (AltinnTesting:UnattendedIdPortenTest and, optionally,
    TargetEnvironments:Trusted) and can start the backend from the same session so the backend inherits them.

    Nothing is persisted: no file, no registry, no user or machine environment variable. Closing the window removes everything.
    The client secret and the reporter's TestID identifier are read as SecureString and never echoed or written anywhere.

    AltinnProfileConfirmed stays false unless -ReporterProfileConfirmed is passed AND you type the confirmation. Only do that after you
    have checked that the synthetic TestID person has an Altinn TT02 profile; nothing here can check it for you.

    The ID-porten TEST client id, scopes, app (buf/skolenaervaer), environment (Test), reporter reference and approved synthetic identity set
    are NOT backend settings: set them in Integration Quality Review → Altinn test configuration.

    This script makes no network call and never contacts ID-porten or Altinn.

.EXAMPLE
    .\configure-skolenaervaer-tt02-local.ps1 -RedirectUri https://localhost:5001/idporten-test/callback
    Prompts for the client secret and the reporter's synthetic TestID identifier; profile confirmation stays false.

.EXAMPLE
    .\configure-skolenaervaer-tt02-local.ps1 -Status
    Shows which settings are present in this session (secrets as Set/Not set only).
#>
[CmdletBinding()]
param(
    [string]$RedirectUri,
    [System.Security.SecureString]$ClientSecret,
    [System.Security.SecureString]$ReporterIdentifier,
    [string]$ReporterAlias = 'reporter-a',
    [string]$ReporterLabel = 'Skolenærvær reporter A',
    [switch]$ReporterProfileConfirmed,
    [string]$TrustedEnvironmentId,
    [string]$TrustedDisplayName,
    [string]$TrustedEnvironmentType = 'Development',
    [string]$TrustedTargetUrl,
    [string[]]$TrustedIntegrationIds,
    [switch]$StartBackend,
    [switch]$Status,
    [switch]$Clear
)

$ErrorActionPreference = 'Stop'
$prefix = 'AltinnTesting__UnattendedIdPortenTest__'
$reporter = "${prefix}Reporters__0__"
$trusted = 'TargetEnvironments__Trusted__0__'
$secretNames = @("${prefix}ClientSecret", "${reporter}TestIdPersonIdentifier")

function Set-SessionVariable([string]$Name, [string]$Value) {
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

function Get-SessionVariable([string]$Name) {
    [Environment]::GetEnvironmentVariable($Name, 'Process')
}

# SecureString → process variable without echoing; the unmanaged copy is zeroed immediately.
function Set-SecretVariable([string]$Name, [System.Security.SecureString]$Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { Set-SessionVariable $Name ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

# Synthetic Test-Norge identifier: 11 digits with month + 80 (81–92). The value is checked here and never printed.
function Test-SyntheticIdentifier([System.Security.SecureString]$Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        if ($plain -notmatch '^\d{11}$') { return $false }
        $month = [int]$plain.Substring(2, 2)
        return ($month -ge 81 -and $month -le 92)
    }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Show-Status {
    Write-Host 'Skolenærvær TT02 settings in this PowerShell session (process scope only):'
    $rows = @(
        @("${prefix}Enabled", $false), @("${prefix}RedirectUri", $false), @("${prefix}ClientSecret", $true),
        @("${reporter}Alias", $false), @("${reporter}Label", $false), @("${reporter}TestIdPersonIdentifier", $true),
        @("${reporter}AltinnProfileConfirmed", $false), @("${trusted}EnvironmentId", $false), @("${trusted}EnvironmentType", $false),
        @("${trusted}ExecutionAllowed", $false), @("${trusted}TargetUrl", $false)
    )
    foreach ($row in $rows) {
        $value = Get-SessionVariable $row[0]
        $shown = if ([string]::IsNullOrEmpty($value)) { 'Not set' } elseif ($row[1]) { 'Set (value not shown)' } else { $value }
        Write-Host ('  {0,-70} {1}' -f $row[0], $shown)
    }
    $index = 0
    while (-not [string]::IsNullOrEmpty((Get-SessionVariable "${trusted}IntegrationIds__$index"))) {
        Write-Host ('  {0,-70} {1}' -f "${trusted}IntegrationIds__$index", (Get-SessionVariable "${trusted}IntegrationIds__$index"))
        $index++
    }
    Write-Host 'Client id, scopes, app, environment, reporter reference and synthetic identity set: Integration Quality Review → Altinn test configuration.'
}

if ($Clear) {
    Get-ChildItem Env: | Where-Object { $_.Name.StartsWith($prefix) -or $_.Name.StartsWith($trusted) } | ForEach-Object { Set-SessionVariable $_.Name $null }
    Write-Host 'Cleared the Skolenærvær TT02 settings from this session.'
    return
}
if ($Status) { Show-Status; return }

# Redirect URI: the exact URI registered for the ID-porten TEST client; https, or http on loopback (the backend enforces the same rule).
if ([string]::IsNullOrWhiteSpace($RedirectUri)) { $RedirectUri = Read-Host 'Registered ID-porten TEST redirect URI' }
$redirect = $null
if (-not [Uri]::TryCreate($RedirectUri.Trim(), [UriKind]::Absolute, [ref]$redirect)) { throw 'The redirect URI is not an absolute URI.' }
$loopback = $redirect.IsLoopback
if (-not ($redirect.Scheme -eq 'https' -or ($redirect.Scheme -eq 'http' -and $loopback))) { throw 'The redirect URI must be https, or http on loopback.' }
if ($redirect.UserInfo -or $redirect.Fragment) { throw 'The redirect URI must not contain user info or a fragment.' }

if ($ReporterAlias -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*$') { throw 'The reporter alias must be lowercase letters, digits and hyphens (e.g. reporter-a).' }

if (-not $ClientSecret) { $ClientSecret = Read-Host 'ID-porten TEST client secret (not shown)' -AsSecureString }
if ($ClientSecret.Length -eq 0) { throw 'No client secret was entered.' }
if (-not $ReporterIdentifier) { $ReporterIdentifier = Read-Host "Synthetic TestID person identifier for $ReporterAlias (not shown)" -AsSecureString }
if (-not (Test-SyntheticIdentifier $ReporterIdentifier)) { throw 'The reporter identifier is not a synthetic Test-Norge identifier (11 digits, month + 80). Real identifiers are never used.' }

$profileConfirmed = $false
if ($ReporterProfileConfirmed) {
    Write-Host "You are about to mark the Altinn TT02 profile of $ReporterAlias as confirmed."
    Write-Host 'Only do this after checking that this synthetic TestID person has an Altinn TT02 profile. Nothing here can check it.'
    $typed = Read-Host "Type $ReporterAlias to confirm"
    if ($typed -ceq $ReporterAlias) { $profileConfirmed = $true } else { Write-Host 'Not confirmed: AltinnProfileConfirmed stays false.' }
}

Set-SessionVariable "${prefix}Enabled" 'true'
Set-SessionVariable "${prefix}RedirectUri" $redirect.AbsoluteUri
Set-SecretVariable "${prefix}ClientSecret" $ClientSecret
Set-SessionVariable "${reporter}Alias" $ReporterAlias
Set-SessionVariable "${reporter}Label" $ReporterLabel
Set-SecretVariable "${reporter}TestIdPersonIdentifier" $ReporterIdentifier
Set-SessionVariable "${reporter}AltinnProfileConfirmed" ($(if ($profileConfirmed) { 'true' } else { 'false' }))

# Trusted target enrolment (backend-authoritative): exact environment id and exact integration ids, never Production, never a wildcard.
if ($TrustedEnvironmentId) {
    if ($TrustedEnvironmentType -match '^(?i)prod') { throw 'A Production environment is never enrolled for execution.' }
    if (-not $TrustedDisplayName) { throw 'A trusted environment needs -TrustedDisplayName.' }
    if (-not $TrustedIntegrationIds -or $TrustedIntegrationIds.Count -eq 0) { throw 'Name the exact integration id(s) to enrol with -TrustedIntegrationIds.' }
    foreach ($id in $TrustedIntegrationIds) { if ($id -match '[*?]' -or [string]::IsNullOrWhiteSpace($id)) { throw "Integration ids must be exact (no wildcard): '$id'." } }
    Set-SessionVariable "${trusted}EnvironmentId" $TrustedEnvironmentId.Trim()
    Set-SessionVariable "${trusted}DisplayName" $TrustedDisplayName.Trim()
    Set-SessionVariable "${trusted}EnvironmentType" $TrustedEnvironmentType.Trim()
    Set-SessionVariable "${trusted}ExecutionAllowed" 'true'
    if ($TrustedTargetUrl) { Set-SessionVariable "${trusted}TargetUrl" $TrustedTargetUrl.Trim() }
    for ($i = 0; $i -lt $TrustedIntegrationIds.Count; $i++) { Set-SessionVariable "${trusted}IntegrationIds__$i" $TrustedIntegrationIds[$i].Trim() }
}

Show-Status
if (-not $profileConfirmed) {
    Write-Host "Reporter profile: Not confirmed. After checking the person's Altinn TT02 profile, rerun with -ReporterProfileConfirmed."
}

if ($StartBackend) {
    $repo = Split-Path -Parent $PSScriptRoot
    $project = Join-Path $repo 'backend\BirkNext.Api'
    $busy = Get-NetTCPConnection -State Listen -LocalPort 5000 -ErrorAction SilentlyContinue
    if ($busy) { throw 'Port 5000 is already in use. Stop the running backend first: a backend started earlier runs the code and settings it started with.' }
    Write-Host "Starting the backend on commit $(git -C $repo rev-parse --short HEAD) with these session settings…"
    dotnet run --project $project
}
else {
    Write-Host 'Restart the backend FROM THIS WINDOW (it inherits these settings), then read the public TT02 metadata again:'
    Write-Host '  dotnet run --project AIAssisted\backend\BirkNext.Api   (or rerun this script with -StartBackend)'
}
