# Self-contained tests for scripts/check-blazor-integrity.ps1 (no Pester; Windows PowerShell 5.1 and pwsh). Synthetic wwwroot folders under
# the temp directory only. Exit code = number of failures.
$ErrorActionPreference = "Stop"
$check = Join-Path $PSScriptRoot "..\check-blazor-integrity.ps1"
$script:failures = 0
function Check([string]$name, [bool]$condition, [string]$detail = "") {
    if ($condition) { Write-Host "PASS $name" -ForegroundColor Green }
    else { $script:failures++; Write-Host "FAIL $name $detail" -ForegroundColor Red }
}
function Sri([byte[]]$b) { $s = [Security.Cryptography.SHA256]::Create(); try { 'sha256-' + [Convert]::ToBase64String($s.ComputeHash($b)) } finally { $s.Dispose() } }
function Gz([string]$path, [byte[]]$b) {
    $f = [IO.File]::Create($path); $z = New-Object IO.Compression.GZipStream($f, [IO.Compression.CompressionMode]::Compress)
    try { $z.Write($b, 0, $b.Length) } finally { $z.Dispose(); $f.Dispose() }
}
$base = Join-Path ([IO.Path]::GetTempPath()) ("bn-sri-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$enc = New-Object Text.UTF8Encoding($false)

# A wwwroot like a real publish: runtime, an assembly and a satellite assembly, each with a .gz copy, and the boot manifest naming their hashes.
function New-Wwwroot([string]$name) {
    $w = Join-Path $base $name; $fw = Join-Path $w '_framework'
    New-Item -ItemType Directory -Force (Join-Path $fw 'nb') | Out-Null
    $files = [ordered]@{ 'dotnet.native.wasm' = 'runtime-8.0.33'; 'App.wasm' = 'app'; 'nb/App.resources.wasm' = 'nb' }
    $sri = @{}
    foreach ($k in $files.Keys) {
        $bytes = $enc.GetBytes($files[$k]); $p = Join-Path $fw $k
        [IO.File]::WriteAllBytes($p, $bytes); Gz "$p.gz" $bytes; $sri[$k] = Sri $bytes
    }
    $boot = @"
{ "mainAssemblyName": "App", "resources": { "hash": "sha256-notafile",
  "wasmNative": { "dotnet.native.wasm": "$($sri['dotnet.native.wasm'])" },
  "assembly": { "App.wasm": "$($sri['App.wasm'])" },
  "satelliteResources": { "nb": { "App.resources.wasm": "$($sri['nb/App.resources.wasm'])" } } } }
"@
    [IO.File]::WriteAllText((Join-Path $fw 'blazor.boot.json'), $boot, $enc)
    return $w
}
function Run([string]$w) { & $check -Wwwroot $w -PassThru }

try {
    $w = New-Wwwroot "ok"; $r = Run $w
    Check "a consistent publish passes (runtime, assembly, satellite assembly, .gz copies)" ($r.Problems.Count -eq 0 -and $r.Checked -eq 3) (($r.Problems -join '; ') + " checked=$($r.Checked)")

    # The real failure: boot manifest for the new runtime pack, the old uncompressed runtime kept.
    $w = New-Wwwroot "stale"; [IO.File]::WriteAllBytes((Join-Path $w '_framework/dotnet.native.wasm'), $enc.GetBytes('runtime-8.0.32'))
    $r = Run $w
    Check "a stale uncompressed runtime fails by name" (@($r.Problems | Where-Object { $_ -eq 'integrity mismatch: _framework/dotnet.native.wasm' }).Count -eq 1) ($r.Problems -join '; ')

    $w = New-Wwwroot "gz"; Gz (Join-Path $w '_framework/App.wasm.gz') $enc.GetBytes('other')
    $r = Run $w
    Check "a precompressed copy with other content fails (a server may serve it instead)" (@($r.Problems | Where-Object { $_ -eq 'integrity mismatch: _framework/App.wasm.gz' }).Count -eq 1) ($r.Problems -join '; ')

    $w = New-Wwwroot "sat"; [IO.File]::WriteAllBytes((Join-Path $w '_framework/nb/App.resources.wasm'), $enc.GetBytes('changed'))
    $r = Run $w
    Check "satellite assemblies are checked under their culture folder" (@($r.Problems | Where-Object { $_ -eq 'integrity mismatch: _framework/nb/App.resources.wasm' }).Count -eq 1) ($r.Problems -join '; ')

    $w = New-Wwwroot "missing"; Remove-Item (Join-Path $w '_framework/App.wasm')
    $r = Run $w
    Check "a listed file that is missing fails" (@($r.Problems | Where-Object { $_ -eq 'missing: _framework/App.wasm' }).Count -eq 1) ($r.Problems -join '; ')

    # Exit codes through a separate process (as the build script calls it).
    $hostExe = (Get-Process -Id $PID).Path
    $w = New-Wwwroot "exit-ok"; & $hostExe -NoProfile -ExecutionPolicy Bypass -File $check -Wwwroot $w | Out-Null
    Check "exit 0 when consistent" ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
    $w = New-Wwwroot "exit-bad"; [IO.File]::WriteAllBytes((Join-Path $w '_framework/dotnet.native.wasm'), $enc.GetBytes('x'))
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $check -Wwwroot $w | Out-Null
    Check "exit 1 on a mismatch" ($LASTEXITCODE -eq 1) "exit $LASTEXITCODE"
    $w = Join-Path $base "noboot"; New-Item -ItemType Directory -Force $w | Out-Null
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $check -Wwwroot $w | Out-Null
    Check "exit 1 without blazor.boot.json" ($LASTEXITCODE -eq 1) "exit $LASTEXITCODE"
}
finally {
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "$script:failures failure(s)"
exit $script:failures
