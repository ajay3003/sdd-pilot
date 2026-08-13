# Setup scoped CSS for local development
# Run this after 'dotnet build' if the Task Explorer styling appears broken

param(
    [string]$BirkNextWebPath = "$PSScriptRoot/BirkNext.Web"
)

$sourceCss = Join-Path $BirkNextWebPath "obj/Debug/net8.0/scopedcss/bundle/BirkNext.Web.styles.css"
$destCss = Join-Path $BirkNextWebPath "wwwroot/BirkNext.Web.styles.css"

if (Test-Path $sourceCss) {
    Write-Host "Copying scoped CSS bundle to wwwroot for development..." -ForegroundColor Green
    Copy-Item $sourceCss $destCss -Force
    Write-Host "Done: $destCss" -ForegroundColor Green
} else {
    Write-Host "Error: Could not find scoped CSS bundle at $sourceCss" -ForegroundColor Red
    Write-Host "Please run dotnet build BirkNext.Web first." -ForegroundColor Yellow
    exit 1
}
