#!/usr/bin/env pwsh
<#
.SYNOPSIS
    M2LB Public Configuration Discovery - Map actual public sources

.DESCRIPTION
    Systematically explores M2LB public assets to identify where MSAL/auth config is exposed.
    Does not assume any specific structure - discovers what's actually there.

.NOTES
    Run against: https://m2lbdev.bufetat.no/
    Output directory created automatically
#>

param(
    [string]$TargetUrl = "https://m2lbdev.bufetat.no",
    [string]$OutputDir = "./m2lb-discovery"
)

$ErrorActionPreference = "SilentlyContinue"
$WarningPreference = "SilentlyContinue"

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

Write-Host "[M2LB Public Configuration Discovery]" -ForegroundColor Cyan
Write-Host "Systematically mapping public assets and config sources" -ForegroundColor Cyan
Write-Host ""
Write-Host "Target: $TargetUrl" -ForegroundColor Yellow
Write-Host "Output: $OutputDir" -ForegroundColor Yellow
Write-Host ""

$fileCount = 0

# -----------------------------------------------------------------------------
# PHASE 1: Fetch main shell and analyze
# -----------------------------------------------------------------------------
Write-Host "[PHASE 1] Analyzing main shell" -ForegroundColor Green

try {
    $shell = Invoke-WebRequest -Uri $TargetUrl -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    $shell.Content | Out-File "$OutputDir/00-shell.html" -Encoding UTF8
    $fileCount++
    Write-Host "  [OK] Shell fetched ($(($shell.Content.Length/1KB).ToString('F2'))KB)"

    # Extract all script sources
    $scriptRefs = @()
    [regex]::Matches($shell.Content, '<script[^>]*src="([^"]+)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase) | ForEach-Object {
        $scriptRefs += $_.Groups[1].Value
    }
    Write-Host "  [OK] Found $($scriptRefs.Count) script references"

    if ($scriptRefs.Count -gt 0) {
        Write-Host "    Scripts referenced:" -ForegroundColor Gray
        $scriptRefs | ForEach-Object {
            Write-Host "      - $_" -ForegroundColor Gray
        }
    }

    # Look for config/auth references
    $authPatterns = @(
        'appsettings',
        'msal',
        'auth',
        'config',
        'clientid|client.id|client_id',
        'authority',
        'tenantid|tenant.id|tenant_id',
        'redirecturi|redirect.uri|redirect_uri'
    )

    $matches = @()
    foreach ($pattern in $authPatterns) {
        $found = [regex]::Matches($shell.Content, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($found.Count -gt 0) {
            $matches += @{ Pattern = $pattern; Count = $found.Count }
        }
    }

    if ($matches.Count -gt 0) {
        Write-Host "  [OK] Found auth-related patterns:" -ForegroundColor Green
        $matches | ForEach-Object {
            Write-Host "      - '$($_.Pattern)': $($_.Count) occurrences" -ForegroundColor Green
        }
    }
} catch {
    Write-Host "  [ERROR] Error fetching shell: $_" -ForegroundColor Red
}

Write-Host ""

# -----------------------------------------------------------------------------
# PHASE 2: Probe common public paths
# -----------------------------------------------------------------------------
Write-Host "[PHASE] PHASE 2: Probing common public paths" -ForegroundColor Green

$probePaths = @(
    # Config files
    "/appsettings.json",
    "/appsettings.development.json",
    "/appsettings.local.json",
    "/config.json",
    "/configuration.json",

    # Auth config
    "/auth.config.json",
    "/msal.config.json",
    "/auth/config.json",
    "/config/auth.json",

    # Blazor specific
    "/_framework/",
    "/_framework/blazor.boot.json",
    "/_framework/blazor.web.js",
    "/blazor.boot.json",
    "/wwwroot/blazor.boot.json",

    # Well-known endpoints
    "/.well-known/openid-configuration",
    "/.well-known/oauth-authorization-server",

    # Root config
    "/config/",
    "/settings/",
    "/api/config",
    "/api/settings"
)

$probeResults = @()

foreach ($path in $probePaths) {
    $url = "$TargetUrl$path"
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5 -ErrorAction SilentlyContinue

        if ($response.StatusCode -eq 200) {
            $size = [math]::Round($response.Content.Length / 1KB, 2)
            Write-Host "  [OK] $path ($($response.StatusCode) - $($size)KB)" -ForegroundColor Green

            # Save the content
            $filename = ($path -replace '/', '_').TrimStart('_')
            if (-not $filename.EndsWith('.json') -and -not $filename.EndsWith('.js')) {
                $filename = "$filename.html"
            }
            $response.Content | Out-File "$OutputDir/01-probe_$filename" -Encoding UTF8
            $fileCount++

            $probeResults += @{ Path = $path; StatusCode = $response.StatusCode; Size = $size }
        }
    } catch {
        # Silently continue - 404s are expected
    }
}

if ($probeResults.Count -eq 0) {
    Write-Host "  [INFO] No common config paths responded with 200" -ForegroundColor Yellow
}

Write-Host ""

# -----------------------------------------------------------------------------
# PHASE 3: Inspect _framework directory contents
# -----------------------------------------------------------------------------
Write-Host "[PHASE] PHASE 3: Inspecting _framework directory" -ForegroundColor Green

try {
    $frameworkUrl = "$TargetUrl/_framework/"
    $response = Invoke-WebRequest -Uri $frameworkUrl -UseBasicParsing -TimeoutSec 5

    if ($response.StatusCode -eq 200) {
        Write-Host "  [OK] _framework directory is public (listing may be disabled)" -ForegroundColor Green
        $response.Content | Out-File "$OutputDir/02-framework-listing.html" -Encoding UTF8
        $fileCount++
    }
} catch {
    Write-Host "  [INFO] _framework directory not directly accessible" -ForegroundColor Yellow
}

Write-Host ""

# -----------------------------------------------------------------------------
# PHASE 4: Analysis of discovered sources
# -----------------------------------------------------------------------------
Write-Host "[PHASE] PHASE 4: Analyzing discovered sources" -ForegroundColor Green

$configFiles = Get-ChildItem "$OutputDir" -Filter "*.json" | Where-Object { $_.Name -notmatch '\.lock$' }

foreach ($file in $configFiles) {
    Write-Host "  Analyzing $($file.Name)..." -ForegroundColor Gray

    try {
        $content = Get-Content $file.FullName -Raw
        $json = $content | ConvertFrom-Json

        # Look for auth-related keys
        $authKeys = @()
        $json.PSObject.Properties | ForEach-Object {
            if ($_.Name -match 'auth|msal|oauth|client|tenant|authority|redirect') {
                $authKeys += $_.Name
            }
        }

        if ($authKeys.Count -gt 0) {
            Write-Host "    [OK] Found auth-related keys: $($authKeys -join ', ')" -ForegroundColor Green
        }

        # Save analysis
        @{
            File = $file.Name
            AuthKeys = $authKeys
            FullContent = $json | ConvertTo-Json -Depth 100
        } | ConvertTo-Json -Depth 100 | Out-File "$OutputDir/03-analysis_$($file.Name)" -Encoding UTF8

    } catch {
        Write-Host "    ⚠ Could not parse as JSON: $_" -ForegroundColor Yellow
    }
}

Write-Host ""

# -----------------------------------------------------------------------------
# PHASE 5: Summary
# -----------------------------------------------------------------------------
Write-Host "[PHASE] PHASE 5: Discovery Summary" -ForegroundColor Green
Write-Host "  Files collected: $fileCount" -ForegroundColor Cyan

$files = Get-ChildItem "$OutputDir" -File
$files | ForEach-Object {
    $size = [math]::Round($_.Length / 1KB, 2)
    Write-Host "    - $($_.Name) ($($size)KB)" -ForegroundColor Gray
}

Write-Host ""
Write-Host "[============================================================]" -ForegroundColor Cyan
Write-Host "|  Next Steps                                               |" -ForegroundColor Cyan
Write-Host "[============================================================╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "1. Review files in: $OutputDir" -ForegroundColor Yellow
Write-Host "2. Identify where MSAL/auth config is actually stored" -ForegroundColor Yellow
Write-Host "3. Look for patterns:" -ForegroundColor Yellow
Write-Host "   - clientId / clientID / client_id" -ForegroundColor Gray
Write-Host "   - authority / authorityUrl / login endpoint" -ForegroundColor Gray
Write-Host "   - tenantId / tenantID / tenant_id" -ForegroundColor Gray
Write-Host "   - redirectUri / redirectURL / callback path" -ForegroundColor Gray
Write-Host "4. Document which source(s) contain the config" -ForegroundColor Yellow
Write-Host ""
