<#
.SYNOPSIS
Verifies that a published Blazor WebAssembly frontend can start: every resource listed in _framework/blazor.boot.json exists and its
SHA-256 equals the integrity value the browser will enforce (Subresource Integrity). Precompressed copies (.gz always; .br when this
PowerShell has BrotliStream) must decompress to the same bytes, because a server may serve them instead of the plain file.

.DESCRIPTION
Why: an incremental publish can regenerate blazor.boot.json and the compressed files for a newer runtime pack while keeping an older
uncompressed dotnet.native.wasm (copy skipped as "newer"). The browser then refuses the runtime and the app stops at "An unhandled error
has occurred" — the package looks complete, every file hash in package-manifest.json is consistent, and only a browser notices.
Reports file names only. Exit 0 = consistent, 1 = at least one mismatch or missing file.

.PARAMETER Wwwroot
The published wwwroot folder (contains _framework/blazor.boot.json).
#>
param(
    [Parameter(Mandatory)] [string] $Wwwroot,
    [switch] $PassThru
)
$ErrorActionPreference = 'Stop'

function Get-Sri([byte[]] $bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { 'sha256-' + [Convert]::ToBase64String($sha.ComputeHash($bytes)) } finally { $sha.Dispose() }
}

function Expand-Bytes([string] $path, [string] $kind) {
    $in = [IO.File]::OpenRead($path)
    try {
        $stream = if ($kind -eq 'gz') { New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress) }
                  else { New-Object IO.Compression.BrotliStream($in, [IO.Compression.CompressionMode]::Decompress) }
        $out = New-Object IO.MemoryStream
        try { $stream.CopyTo($out); , $out.ToArray() } finally { $stream.Dispose(); $out.Dispose() }
    }
    finally { $in.Dispose() }
}

$framework = Join-Path $Wwwroot '_framework'
$bootPath = Join-Path $framework 'blazor.boot.json'
if (-not (Test-Path -LiteralPath $bootPath)) { Write-Host "Blazor integrity check FAILED: blazor.boot.json not found under $Wwwroot" -ForegroundColor Red; exit 1 }
$boot = Get-Content -LiteralPath $bootPath -Raw | ConvertFrom-Json
$brotli = $null -ne ('System.IO.Compression.BrotliStream' -as [type])

# Every (relative file, integrity) pair: sections map file → "sha256-…"; satellite sections map culture → { file → "sha256-…" }.
$entries = New-Object System.Collections.Generic.List[object]
foreach ($section in $boot.resources.PSObject.Properties) {
    if ($section.Value -isnot [System.Management.Automation.PSCustomObject]) { continue }
    foreach ($item in $section.Value.PSObject.Properties) {
        if ($item.Value -is [string]) { $entries.Add([pscustomobject]@{ File = $item.Name; Integrity = $item.Value }) }
        elseif ($item.Value -is [System.Management.Automation.PSCustomObject]) {
            foreach ($nested in $item.Value.PSObject.Properties) {
                if ($nested.Value -is [string]) { $entries.Add([pscustomobject]@{ File = "$($item.Name)/$($nested.Name)"; Integrity = $nested.Value }) }
            }
        }
    }
}

$problems = New-Object System.Collections.Generic.List[string]
$checked = 0
foreach ($e in $entries) {
    if ($e.Integrity -notlike 'sha256-*') { continue }
    $path = Join-Path $framework ($e.File -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $path)) { $problems.Add("missing: _framework/$($e.File)"); continue }
    if ((Get-Sri ([IO.File]::ReadAllBytes($path))) -ne $e.Integrity) { $problems.Add("integrity mismatch: _framework/$($e.File)") }
    foreach ($kind in @('gz', 'br')) {
        $compressed = "$path.$kind"
        if (-not (Test-Path -LiteralPath $compressed)) { continue }
        if ($kind -eq 'br' -and -not $brotli) { continue }
        if ((Get-Sri (Expand-Bytes $compressed $kind)) -ne $e.Integrity) { $problems.Add("integrity mismatch: _framework/$($e.File).$kind") }
    }
    $checked++
}

if ($PassThru) { return [pscustomobject]@{ Checked = $checked; Problems = @($problems); BrotliChecked = $brotli } }
if ($checked -eq 0) { Write-Host "Blazor integrity check FAILED: blazor.boot.json lists no resources with an integrity value: $bootPath" -ForegroundColor Red; exit 1 }
if ($problems.Count -gt 0) {
    Write-Host "Blazor integrity check FAILED ($($problems.Count) of $checked resources). The browser would refuse these files:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  $_" }
    Write-Host "Cause is usually a stale incremental publish: delete the frontend's bin/obj and publish again." -ForegroundColor Yellow
    exit 1
}
Write-Host "Blazor integrity check passed: $checked resources match blazor.boot.json$(if (-not $brotli) { ' (.br copies not checked: no BrotliStream in this PowerShell)' })."
exit 0
