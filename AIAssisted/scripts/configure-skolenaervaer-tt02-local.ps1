<#
.SYNOPSIS
    Sets the backend settings for a local, unattended Skolenærvær TT02 run in THIS PowerShell session only.

.DESCRIPTION
    Developer-only helper. It asks for the values that belong to you and your environment, sets them as process-scope environment variables,
    and can start the backend from the same session so the backend inherits them. Every value can change at any time without a code change.

    It asks for:
      Reporter alias          (e.g. reporter-a; the stable name the UI, logs and history use)
      Reporter label          (e.g. Skolenærvær reporter A)
      Reporter synthetic identifier   (a synthetic Test-Norge TestID person: 11 digits, month + 80 — not shown, not stored)
      Profile confirmed?      (only after checking that person's Altinn TT02 profile; default: Not confirmed)
      Client id               (optional here; may instead be set in Integration Quality Review → Altinn test configuration)
      Redirect URI            (the URI registered for the ID-porten TEST client)
      Client secret           (not shown, not stored)
      Approved synthetic child  (optional: alias, label and a synthetic Test-Norge identifier — not shown, not stored; Enter skips it)

    Nothing is persisted to the registry or user/machine environment. Closing the window removes process-scope values.
    This local helper contains approved synthetic default identifiers for reporter-a and child-a. They are test-only values and the script makes no network call.

    What BirkNext discovers from the deployed app (host, process task, data type, endpoints) is NOT configured here: it is read from the app's
    public TT02 metadata ("Read public TT02 metadata") after the backend has started.

.EXAMPLE
    .\configure-skolenaervaer-tt02-local.ps1
    Prompts for every value.

.EXAMPLE
    .\configure-skolenaervaer-tt02-local.ps1 -ReporterIndex 1 -ReporterAlias reporter-b -ReporterLabel 'Skolenærvær reporter B'
    Adds or replaces a second reporter in this session.

.EXAMPLE
    .\configure-skolenaervaer-tt02-local.ps1 -ChildAlias synthetic-child-1 -ChildLabel 'Synthetic child 1'
    Adds an approved synthetic child (known to Person, declared). The run page then resolves it by alias; BirkNext never generates one.

.EXAMPLE
    .\configure-skolenaervaer-tt02-local.ps1 -Status
    Shows which settings are present in this session (secrets and identifiers as Set/Not set only).
#>
[CmdletBinding()]
param(
    [string]$RedirectUri,
    [System.Security.SecureString]$ClientSecret,
    [string]$ClientId,
    [System.Security.SecureString]$ReporterIdentifier,
    [ValidateRange(0, 9)][int]$ReporterIndex = 0,
    [string]$ReporterAlias,
    [string]$ReporterLabel,
    [switch]$ReporterProfileConfirmed,
    [System.Security.SecureString]$ChildIdentifier,
    [ValidateRange(0, 9)][int]$ChildIndex = 0,
    [string]$ChildAlias,
    [string]$ChildLabel,
    [switch]$ChildUnknownToPerson,
    [switch]$ChildDefault,
    [string]$TrustedEnvironmentId,
    [string]$TrustedDisplayName,
    [string]$TrustedEnvironmentType = 'Development',
    [string]$TrustedTargetUrl,
    [string[]]$TrustedIntegrationIds,
    [switch]$NoPrompt,
    [switch]$StartBackend,
    [switch]$Status,
    [switch]$Clear
)

$ErrorActionPreference = 'Stop'
$prefix = 'AltinnTesting__UnattendedIdPortenTest__'
$reporter = "${prefix}Reporters__${ReporterIndex}__"
$trusted = 'TargetEnvironments__Trusted__0__'
$children = 'AltinnTesting__SyntheticChildren__'
$child = "${children}${ChildIndex}__"

# Local test-only defaults. These are approved synthetic identities, never production identities.
$defaultReporterAlias = 'reporter-a'
$defaultReporterLabel = 'Skolenærvær reporter A'
$defaultReporterIdentifier = '05848597165'
$defaultChildAlias = 'child-a'
$defaultChildLabel = 'Skolenærvær test child A'
$defaultChildIdentifier = '12113802888'

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

# Reporter TestID identifier: 11 digits with the synthetic Test-Norge month + 80 convention (81–92).
function Test-ReporterSyntheticIdentifier([System.Security.SecureString]$Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        if ($plain -notmatch '^\d{11}$') { return $false }
        $month = [int]$plain.Substring(2, 2)
        return ($month -ge 81 -and $month -le 92)
    }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

# Approved child identities in Test-Norge can use ordinary-looking 11-digit identifiers.
# The script cannot prove that such an identifier belongs to Test-Norge; approval/known-to-Person is a test-data trust decision.
function Test-ChildSyntheticIdentifier([System.Security.SecureString]$Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        return ($plain -match '^\d{11}$')
    }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

# Asks only when the value was not passed and prompting is allowed; Enter keeps the default.
function Read-Value([string]$Prompt, [string]$Current, [string]$Default) {
    if (-not [string]::IsNullOrWhiteSpace($Current)) { return $Current.Trim() }
    if ($NoPrompt) { return $Default }
    $answer = Read-Host $(if ($Default) { "$Prompt [$Default]" } else { $Prompt })
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default } else { return $answer.Trim() }
}

function Show-Status {
    Write-Host 'Skolenærvær TT02 settings in this PowerShell session (process scope only):'
    $rows = @(@("${prefix}Enabled", $false), @("${prefix}RedirectUri", $false), @("${prefix}ClientId", $false), @("${prefix}ClientSecret", $true))
    for ($i = 0; $i -le 9; $i++) {
        $r = "${prefix}Reporters__${i}__"
        if ([string]::IsNullOrEmpty((Get-SessionVariable "${r}Alias"))) { continue }
        $rows += , @("${r}Alias", $false); $rows += , @("${r}Label", $false); $rows += , @("${r}TestIdPersonIdentifier", $true); $rows += , @("${r}AltinnProfileConfirmed", $false)
    }
    for ($i = 0; $i -le 9; $i++) {
        $c = "${children}${i}__"
        if ([string]::IsNullOrEmpty((Get-SessionVariable "${c}Alias"))) { continue }
        $rows += , @("${c}Alias", $false); $rows += , @("${c}Label", $false); $rows += , @("${c}TestPersonIdentifier", $true); $rows += , @("${c}KnownToPerson", $false); $rows += , @("${c}Default", $false)
    }
    $rows += @(@("${trusted}EnvironmentId", $false), @("${trusted}EnvironmentType", $false), @("${trusted}ExecutionAllowed", $false), @("${trusted}TargetUrl", $false))
    foreach ($row in $rows) {
        $value = Get-SessionVariable $row[0]
        $shown = if ([string]::IsNullOrEmpty($value)) { 'Not set' } elseif ($row[1]) { 'Set (value not shown)' } else { $value }
        Write-Host ('  {0,-72} {1}' -f $row[0], $shown)
    }
    $index = 0
    while (-not [string]::IsNullOrEmpty((Get-SessionVariable "${trusted}IntegrationIds__$index"))) {
        Write-Host ('  {0,-72} {1}' -f "${trusted}IntegrationIds__$index", (Get-SessionVariable "${trusted}IntegrationIds__$index"))
        $index++
    }
    Write-Host 'Scopes, app, environment, reporter reference and synthetic identity set: Integration Quality Review → Altinn test configuration.'
    Write-Host 'Skolenærvær Testing resolves these values automatically; it never stores another copy.'
    Write-Host 'Process task, data type and endpoints are read from the deployed app ("Read public TT02 metadata"), never configured here.'
}

if ($Clear) {
    Get-ChildItem Env: | Where-Object { $_.Name.StartsWith($prefix) -or $_.Name.StartsWith($trusted) -or $_.Name.StartsWith($children) } | ForEach-Object { Set-SessionVariable $_.Name $null }
    Write-Host 'Cleared the Skolenærvær TT02 settings from this session.'
    return
}
if ($Status) { Show-Status; return }

# Reporter: an alias (stable) and a synthetic identifier behind it (replaceable).
$ReporterAlias = Read-Value 'Reporter alias' $ReporterAlias $(if ($ReporterIndex -eq 0) { $defaultReporterAlias } else { "reporter-$([char](97 + $ReporterIndex))" })
if ($ReporterAlias -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*$') { throw 'The reporter alias must be lowercase letters, digits and hyphens (e.g. reporter-a).' }
$ReporterLabel = Read-Value 'Reporter label' $ReporterLabel $(if ($ReporterIndex -eq 0) { $defaultReporterLabel } else { "Skolenærvær reporter $([char](65 + $ReporterIndex))" })
if (-not $ReporterIdentifier) {
    if ($ReporterIndex -eq 0 -and $ReporterAlias -eq $defaultReporterAlias) {
        $ReporterIdentifier = ConvertTo-SecureString $defaultReporterIdentifier -AsPlainText -Force
        Write-Host "Synthetic TestID identifier for ${ReporterAlias}: using approved local default (value not shown)."
    }
    elseif ($NoPrompt) { throw 'No reporter identifier: pass -ReporterIdentifier or run without -NoPrompt.' }
    else {
        Write-Host 'Example synthetic identifier format: 11 digits (a synthetic Test-Norge person, month + 80). It is not shown.'
        $ReporterIdentifier = Read-Host "Synthetic TestID identifier for $ReporterAlias" -AsSecureString
    }
}
if (-not (Test-ReporterSyntheticIdentifier $ReporterIdentifier)) { throw 'The reporter identifier is not a synthetic Test-Norge TestID identifier (11 digits, month + 80). Real identifiers are never used.' }

# Profile confirmation is a deliberate step: never inferred from the identifier or this script.
$profileConfirmed = $false
if ($ReporterProfileConfirmed -or -not $NoPrompt) {
    Write-Host "Has the Altinn TT02 profile of $ReporterAlias's synthetic person been confirmed? Nothing here can check it."
    $typed = if ($NoPrompt -and -not $ReporterProfileConfirmed) { '' } else { Read-Host "Type $ReporterAlias to mark it confirmed, or press Enter to leave it Not confirmed" }
    if ($typed -ceq $ReporterAlias) { $profileConfirmed = $true } elseif ($typed) { Write-Host 'Not confirmed: the alias did not match.' }
}

# Approved synthetic child (optional): approved test data the run page resolves by alias. Nothing is generated; reporter ≠ child is enforced by the backend.
$ChildAlias = Read-Value 'Approved synthetic child alias (Enter to skip)' $ChildAlias $(if ($ChildIndex -eq 0) { $defaultChildAlias } else { '' })
if ($ChildAlias) {
    if ($ChildAlias -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*$') { throw 'The child alias must be lowercase letters, digits and hyphens (e.g. synthetic-child-1).' }
    $ChildLabel = Read-Value 'Child label' $ChildLabel $(if ($ChildIndex -eq 0 -and $ChildAlias -eq $defaultChildAlias) { $defaultChildLabel } else { $ChildAlias })
    if (-not $ChildIdentifier) {
        if ($ChildIndex -eq 0 -and $ChildAlias -eq $defaultChildAlias) {
            $ChildIdentifier = ConvertTo-SecureString $defaultChildIdentifier -AsPlainText -Force
            Write-Host "Synthetic Test-Norge identifier for ${ChildAlias}: using approved local default (value not shown)."
        }
        elseif ($NoPrompt) { throw 'No child identifier: pass -ChildIdentifier or run without -NoPrompt.' }
        else {
            $ChildIdentifier = Read-Host "Synthetic Test-Norge identifier for $ChildAlias (not shown)" -AsSecureString
        }
    }
    if (-not (Test-ChildSyntheticIdentifier $ChildIdentifier)) { throw 'The child identifier must be exactly 11 digits. Only an approved synthetic Test-Norge identity may be used.' }
}

# Client id: optional here (Integration Quality Review can hold it instead; both set and different is reported as a conflict).
$ClientId = Read-Value 'ID-porten TEST client id (Enter to keep it in Integration Quality Review only)' $ClientId ''

# Redirect URI: the exact URI registered for the ID-porten TEST client; https, or http on loopback (the backend enforces the same rule).
$RedirectUri = Read-Value 'Registered ID-porten TEST redirect URI' $RedirectUri ''
$redirect = $null
if (-not [Uri]::TryCreate("$RedirectUri".Trim(), [UriKind]::Absolute, [ref]$redirect)) { throw 'The redirect URI is not an absolute URI.' }
if (-not ($redirect.Scheme -eq 'https' -or ($redirect.Scheme -eq 'http' -and $redirect.IsLoopback))) { throw 'The redirect URI must be https, or http on loopback.' }
if ($redirect.UserInfo -or $redirect.Fragment) { throw 'The redirect URI must not contain user info or a fragment.' }

if (-not $ClientSecret) {
    if ($NoPrompt) { throw 'No client secret: pass -ClientSecret or run without -NoPrompt.' }
    $ClientSecret = Read-Host 'ID-porten TEST client secret (not shown)' -AsSecureString
}
if ($ClientSecret.Length -eq 0) { throw 'No client secret was entered.' }

Set-SessionVariable "${prefix}Enabled" 'true'
Set-SessionVariable "${prefix}RedirectUri" $redirect.AbsoluteUri
Set-SecretVariable "${prefix}ClientSecret" $ClientSecret
if ($ClientId) { Set-SessionVariable "${prefix}ClientId" $ClientId }
Set-SessionVariable "${reporter}Alias" $ReporterAlias
Set-SessionVariable "${reporter}Label" $ReporterLabel
Set-SecretVariable "${reporter}TestIdPersonIdentifier" $ReporterIdentifier
Set-SessionVariable "${reporter}AltinnProfileConfirmed" ($(if ($profileConfirmed) { 'true' } else { 'false' }))
if ($ChildAlias) {
    Set-SessionVariable "${child}Alias" $ChildAlias
    Set-SessionVariable "${child}Label" $ChildLabel
    Set-SecretVariable "${child}TestPersonIdentifier" $ChildIdentifier
    Set-SessionVariable "${child}KnownToPerson" ($(if ($ChildUnknownToPerson) { 'false' } else { 'true' }))
    Set-SessionVariable "${child}Default" ($(if ($ChildDefault) { 'true' } else { 'false' }))
}

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
    Write-Host "Reporter $ReporterAlias profile: Not confirmed. After checking the person's Altinn TT02 profile, run this again and type the alias when asked."
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
