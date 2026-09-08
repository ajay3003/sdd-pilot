# M2LB Public Configuration Discovery Diagnostic
# Purpose: Identify where MSAL/auth configuration is exposed publicly

$targetUrl = "https://m2lbdev.bufetat.no"
$outputDir = "C:\Users\ajaan\source\sdd-repos\m2lb-discovery"

if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

Write-Host "M2LB Public Configuration Discovery Diagnostic"
Write-Host "============================================="
Write-Host "Target: $targetUrl"
Write-Host ""

# 1. Fetch the main shell
Write-Host "[1/6] Fetching main shell..."
try {
    $shell = Invoke-WebRequest -Uri $targetUrl -UseBasicParsing -TimeoutSec 10
    $shell.Content | Out-File "$outputDir\01-shell.html" -Encoding UTF8
    Write-Host "✓ Shell saved to 01-shell.html"

    # Look for references to config files in shell
    $configRefs = $shell.Content | Select-String -Pattern '(appsettings|config|msal)' -AllMatches
    Write-Host "  Found $(($configRefs.Matches | Measure-Object).Count) potential config references"
} catch {
    Write-Host "✗ Error fetching shell: $_"
}

# 2. Try common config file locations
Write-Host "[2/6] Checking common config file locations..."
$configPaths = @(
    "/appsettings.json",
    "/appsettings.Development.json",
    "/appsettings.Local.json",
    "/config/appsettings.json",
    "/configuration/appsettings.json"
)

foreach ($path in $configPaths) {
    $url = "$targetUrl$path"
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5 -ErrorAction SilentlyContinue
        if ($response.StatusCode -eq 200) {
            Write-Host "✓ Found: $path (status $($response.StatusCode))"
            $response.Content | Out-File "$outputDir\02-config$(($path -replace '/', '_')).json" -Encoding UTF8
        }
    } catch {
        # Silently continue
    }
}

# 3. Check for framework JS files
Write-Host "[3/6] Checking for _framework directory..."
try {
    $frameworkUrl = "$targetUrl/_framework/"
    $framework = Invoke-WebRequest -Uri $frameworkUrl -UseBasicParsing -TimeoutSec 5 -ErrorAction SilentlyContinue
    if ($framework.StatusCode -eq 200) {
        Write-Host "✓ _framework directory is public"
        $framework.Content | Out-File "$outputDir\03-framework-index.html" -Encoding UTF8

        # Look for bootconfig
        if ($framework.Content -match 'blazor\.boot\.json') {
            Write-Host "  - Found reference to blazor.boot.json"
        }
    }
} catch {
    Write-Host "✗ _framework not directly accessible"
}

# 4. Try to fetch blazor.boot.json
Write-Host "[4/6] Checking for Blazor boot configuration..."
$bootPaths = @(
    "/_framework/blazor.boot.json",
    "/blazor.boot.json",
    "/wwwroot/blazor.boot.json"
)

foreach ($path in $bootPaths) {
    $url = "$targetUrl$path"
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5 -ErrorAction SilentlyContinue
        if ($response.StatusCode -eq 200) {
            Write-Host "✓ Found: $path"
            $response.Content | Out-File "$outputDir\04-blazor.boot.json" -Encoding UTF8

            # Parse for config references
            try {
                $bootConfig = $response.Content | ConvertFrom-Json
                Write-Host "  - Boot config contains $(($bootConfig.resources | Get-Member | Measure-Object).Count) resource groups"
            } catch {
                Write-Host "  - Could not parse as JSON"
            }
        }
    } catch {
        # Silently continue
    }
}

# 5. Look for MSAL in JS bundles
Write-Host "[5/6] Scanning for MSAL references..."
try {
    $shell = Get-Content "$outputDir\01-shell.html" -Raw

    $msalRefs = $shell | Select-String -Pattern '(msal|authentication|authorize|client.id|clientId)' -AllMatches
    Write-Host "✓ Found $(($msalRefs.Matches | Measure-Object).Count) MSAL-related references"

    # Extract script references
    $scripts = $shell | Select-String -Pattern '<script[^>]*src="([^"]+)"' -AllMatches
    Write-Host "  - Shell references $($scripts.Matches.Count) external scripts"

    foreach ($match in $scripts.Matches) {
        $scriptSrc = $match.Groups[1].Value
        if ($scriptSrc -match '(app|auth|msal)') {
            Write-Host "    → Potentially relevant: $scriptSrc"
        }
    }
} catch {
    Write-Host "✗ Error scanning for MSAL: $_"
}

# 6. Summary
Write-Host "[6/6] Diagnostic Summary"
Write-Host "======================="
$files = Get-ChildItem $outputDir -File
Write-Host "Files collected: $($files.Count)"
foreach ($file in $files) {
    $size = [math]::Round($file.Length / 1KB, 2)
    Write-Host "  - $($file.Name) ($($size)KB)"
}

Write-Host ""
Write-Host "Next Steps:"
Write-Host "1. Review discovered config files for MSAL/auth settings"
Write-Host "2. Look for ClientId, Authority, TenantId, RedirectUri patterns"
Write-Host "3. Identify whether config is embedded in JS or separate file"
Write-Host "4. Implement config parser based on actual M2LB structure"
