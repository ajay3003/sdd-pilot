<#
.SYNOPSIS
Build the embedded offline translation bundle (runtime/translation of the Windows Tester Package): the embeddable Python runtime without network
modules, CTranslate2 and SentencePiece, the HPLT English -> Norwegian (bokmal) model converted to CTranslate2 int8, the helper, licenses and a
translation-manifest.json with the SHA-256 of every file.

.DESCRIPTION
Every input is pinned in AIAssisted/versioning/native-runtime.lock.json (offlineTranslation). Inputs are taken from -DownloadCache and verified
against their pinned SHA-256; with -AllowDownload a missing input is downloaded from its pinned URL into the cache first (and verified the same way).
This is the only step that ever downloads anything: a tester's PC never does. The finished bundle is checked by starting the helper and translating
a sample sentence. Run sequentially; nothing is installed on this PC.

Exit code 0 = bundle written and verified; 2 = blocked input or failure.
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$DownloadCache,
    [switch]$AllowDownload
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = Split-Path -Parent $PSScriptRoot
$ai = Join-Path $repo 'AIAssisted'
function Sha256([string]$path) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }
function Step([string]$m) { Write-Host "[STEP] $m" -ForegroundColor Cyan }

function Get-PinnedInput([string]$url, [string]$sha256, [string]$fileName) {
    New-Item -ItemType Directory -Force -Path $DownloadCache | Out-Null
    $file = Join-Path $DownloadCache $fileName
    if (-not (Test-Path -LiteralPath $file)) {
        if (-not $AllowDownload) { throw "BLOCKED: $fileName is not in $DownloadCache and -AllowDownload was not given (pinned source: $url)" }
        Write-Host "  downloading pinned $url"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $url -OutFile "$file.part" -UseBasicParsing
        Move-Item -LiteralPath "$file.part" -Destination $file
    }
    $actual = Sha256 $file
    if ($actual -ne $sha256) { throw "BLOCKED: $fileName has SHA-256 $actual, the lock pins $sha256. The file is not used." }
    Write-Host "  [OK] $fileName sha256 verified"
    return $file
}

function Expand-Wheel([string]$wheel, [string]$target) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($wheel, $target)
}

try {
    $lock = (Get-Content (Join-Path $ai 'versioning/native-runtime.lock.json') -Raw | ConvertFrom-Json).offlineTranslation
    if (-not $lock) { throw 'native-runtime.lock.json has no offlineTranslation section' }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $OutDir = (Resolve-Path $OutDir).Path
    $bundle = Join-Path $OutDir 'translation'
    $work = Join-Path $OutDir 'translation-build'
    foreach ($d in @($bundle, $work)) { if (Test-Path -LiteralPath $d) { throw "The folder already exists (never overwritten): $d" } }

    Step 'Pinned inputs'
    $pythonZip = Get-PinnedInput $lock.python.url $lock.python.sha256 ([IO.Path]::GetFileName($lock.python.url))
    $wheels = @{}
    foreach ($p in $lock.wheels.PSObject.Properties) { $wheels[$p.Name] = Get-PinnedInput $p.Value.url $p.Value.sha256 ([IO.Path]::GetFileName($p.Value.url)) }
    $model = @{}
    foreach ($f in $lock.model.files) { $model[$f.name] = Get-PinnedInput $f.url $f.sha256 ("hplt-en-nb-$($lock.model.revision.Substring(0, 12))-" + $f.name) }
    $licenses = @{}
    foreach ($l in $lock.licenses) { $licenses[$l.name] = Get-PinnedInput $l.url $l.sha256 $l.name }

    Step "Embeddable Python $($lock.python.version) (no network modules)"
    $python = Join-Path $bundle 'python'
    [IO.Compression.ZipFile]::ExtractToDirectory($pythonZip, $python)
    foreach ($m in $lock.python.removeModules) { $f = Join-Path $python $m; if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }
    # Only the runtime's own library zip, its folder and the bundled packages; no site, no user site, no PYTHONPATH.
    [IO.File]::WriteAllText((Join-Path $python 'python312._pth'), "python312.zip`r`n.`r`n..\lib`r`n", (New-Object Text.UTF8Encoding($false)))

    Step 'Runtime packages (CTranslate2, SentencePiece)'
    $lib = Join-Path $bundle 'lib'
    $wheelDir = Join-Path $work 'wheels'
    foreach ($name in @('ctranslate2', 'sentencepiece')) { Expand-Wheel $wheels[$name] (Join-Path $wheelDir $name) }
    Copy-Item -Recurse (Join-Path $wheelDir 'ctranslate2/ctranslate2') (Join-Path $lib 'ctranslate2')
    Copy-Item -Recurse (Join-Path $wheelDir 'sentencepiece/sentencepiece') (Join-Path $lib 'sentencepiece')
    Copy-Item -Recurse (Join-Path $wheelDir 'sentencepiece/sentencepiece.libs') (Join-Path $lib 'sentencepiece.libs')
    # CPU only: the CUDA DNN library and the model converters are not shipped.
    foreach ($drop in @('ctranslate2/converters', 'ctranslate2/cudnn64_9.dll')) { $p = Join-Path $lib $drop; if (Test-Path -LiteralPath $p) { Remove-Item -Recurse -Force -LiteralPath $p } }
    Get-ChildItem -LiteralPath $lib -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath $lib -Recurse -File | Where-Object { $_.Extension -in @('.cc', '.pyi') } | Remove-Item -Force
    # ctranslate2.dll imports msvcp140.dll: the copy the SentencePiece wheel vendors (pinned with the wheel) goes beside python.exe.
    $msvcp = Get-ChildItem -LiteralPath (Join-Path $lib 'sentencepiece.libs') -Filter 'msvcp140-*.dll' | Select-Object -First 1
    if ($msvcp) { Copy-Item $msvcp.FullName (Join-Path $python 'msvcp140.dll') }

    Step 'Model: HPLT en->nb (Marian) to CTranslate2 int8'
    $buildLib = Join-Path $work 'build-lib'
    foreach ($name in @('ctranslate2', 'numpy', 'pyyaml')) { Expand-Wheel $wheels[$name] $buildLib }
    Copy-Item -Recurse -Force (Join-Path $wheelDir 'sentencepiece/sentencepiece') (Join-Path $buildLib 'sentencepiece')
    $modelDir = Join-Path $bundle 'model'
    & (Join-Path $python 'python.exe') -I -X utf8 (Join-Path $ai 'tools/offline-translation/convert_model.py') $buildLib $model['model.npz.best-chrf.npz'] $model['model.en-nb.vocab'] $modelDir
    if ($LASTEXITCODE -ne 0) { throw 'The model conversion failed' }
    Copy-Item $model['model.en-nb.spm'] (Join-Path $modelDir 'sentencepiece.model')

    Step 'Helper, licenses and attribution'
    New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'helper'), (Join-Path $bundle 'licenses') | Out-Null
    Copy-Item (Join-Path $ai 'tools/offline-translation/birknext_translate.py') (Join-Path $bundle 'helper/birknext_translate.py')
    foreach ($name in $licenses.Keys) { Copy-Item $licenses[$name] (Join-Path $bundle "licenses/$name") }
    Copy-Item (Join-Path $python 'LICENSE.txt') (Join-Path $bundle 'licenses/Python-LICENSE.txt')
    Copy-Item $model['README.md'] (Join-Path $bundle 'licenses/HPLT-model-README.md')
    $notice = @(
        'BirkNext embedded offline translation - third-party components',
        '',
        "Model      $($lock.model.id) revision $($lock.model.revision)  $($lock.model.spdx)",
        "           $($lock.model.attribution)",
        "Engine     CTranslate2 $($lock.wheels.ctranslate2.version) (OpenNMT/SYSTRAN)  MIT  licenses/CTranslate2-LICENSE.txt",
        '           ctranslate2.dll and libiomp5md.dll include Intel oneMKL / OpenMP runtime code distributed in the upstream wheel (Intel Simplified Software License).',
        "Tokenizer  SentencePiece $($lock.wheels.sentencepiece.version) (Google)  Apache-2.0  licenses/SentencePiece-LICENSE.txt",
        "Runtime    Python $($lock.python.version) embeddable package  PSF-2.0  licenses/Python-LICENSE.txt (network modules removed)",
        '           msvcp140.dll / vcruntime140*.dll: Microsoft Visual C++ runtime (redistributable).',
        '',
        'Machine translation can be wrong. A tester reviews every translated text; the original text is always kept.'
    ) -join "`r`n"
    [IO.File]::WriteAllText((Join-Path $bundle 'licenses/NOTICE.txt'), $notice + "`r`n", (New-Object Text.UTF8Encoding($false)))

    Step 'translation-manifest.json'
    $files = Get-ChildItem -LiteralPath $bundle -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($bundle.Length + 1).Replace('\', '/'); sha256 = (Sha256 $_.FullName); size = $_.Length } }
    $manifest = [ordered]@{
        format = 1
        protocol = $lock.protocol
        engine = [ordered]@{ name = 'CTranslate2'; version = $lock.wheels.ctranslate2.version; tokenizer = "SentencePiece $($lock.wheels.sentencepiece.version)"; python = $lock.python.version; computeType = 'int8'; device = 'cpu' }
        model = [ordered]@{ id = $lock.model.id; revision = $lock.model.revision; source = $lock.model.source; target = $lock.model.target; license = $lock.model.spdx; attribution = $lock.model.attribution }
        python = 'python/python.exe'
        helper = 'helper/birknext_translate.py'
        modelDirectory = 'model'
        sentencePiece = 'model/sentencepiece.model'
        files = @($files)
    }
    [IO.File]::WriteAllText((Join-Path $bundle 'translation-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))

    Step 'Verify: start the helper and translate a sample'
    $probe = '{"op":"probe"}' + "`n" + '{"op":"exit"}' + "`n"
    $out = $probe | & (Join-Path $python 'python.exe') -I -X utf8 (Join-Path $bundle 'helper/birknext_translate.py') --model $modelDir --spm (Join-Path $modelDir 'sentencepiece.model') --threads 2
    $answers = @($out | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    if ($answers.Count -lt 2 -or -not $answers[0].ready -or -not $answers[1].ok -or -not $answers[1].sample) { throw "The bundle does not translate: $($out -join ' | ')" }
    Write-Host "  [OK] sample: $($answers[1].sample)"
    $size = (Get-ChildItem -LiteralPath $bundle -Recurse -File | Measure-Object Length -Sum).Sum
    Remove-Item -Recurse -Force -LiteralPath $work
    Write-Host ("[OK] {0} ({1} files, {2:N0} MB)" -f $bundle, $files.Count, ($size / 1MB)) -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "[ERROR] $_" -ForegroundColor Red
    exit 2
}
