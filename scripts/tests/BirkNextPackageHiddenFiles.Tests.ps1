# Self-contained tests for scripts/check-package-hidden-files.ps1 (no Pester; Windows PowerShell 5.1 and pwsh). Synthetic folders under the
# temp directory only. Exit code = number of failures.
$ErrorActionPreference = "Stop"
$check = Join-Path $PSScriptRoot "..\check-package-hidden-files.ps1"
$script:failures = 0
function Check([string]$name, [bool]$condition, [string]$detail = "") {
    if ($condition) { Write-Host "PASS $name" -ForegroundColor Green }
    else { $script:failures++; Write-Host "FAIL $name $detail" -ForegroundColor Red }
}
$base = Join-Path ([IO.Path]::GetTempPath()) ("bn-hidden-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$template = Join-Path $base "template.env"
New-Item -ItemType Directory -Force $base | Out-Null
Set-Content -LiteralPath $template -Value "POSTGRES_DB=x" -NoNewline -Encoding ascii

function New-Package([string]$name) {
    $p = Join-Path $base $name
    foreach ($d in 'AIAssisted/backend/.playwright/node', 'AIAssisted/frontend/wwwroot', 'scripts') { New-Item -ItemType Directory -Force (Join-Path $p $d) | Out-Null }
    Copy-Item -LiteralPath $template (Join-Path $p 'AIAssisted/.env')
    Copy-Item -LiteralPath $template (Join-Path $p 'AIAssisted/.env.example')
    Set-Content (Join-Path $p 'AIAssisted/backend/.playwright/node/LICENSE') "l"
    Set-Content (Join-Path $p 'AIAssisted/frontend/.graphqlrc.json') "{}"
    Set-Content (Join-Path $p 'AIAssisted/frontend/wwwroot/.gitkeep') ""
    Set-Content (Join-Path $p 'AIAssisted/backend/appsettings.json') "{}"
    return $p
}
function Run([string]$p) { & $check -PackageRoot $p -EnvTemplate $template -PassThru }

try {
    $p = New-Package "ok"
    $r = Run $p
    Check "the package's required hidden files (.env = template, .env.example, Playwright driver, .gitkeep, .graphqlrc.json) pass" ($r.Count -eq 0) ($r -join "; ")

    $p = New-Package "envdiff"; Set-Content -LiteralPath (Join-Path $p 'AIAssisted/.env') "POSTGRES_PASSWORD=real" -Encoding ascii
    $r = Run $p
    Check "a .env that differs from the checked-in template fails (a workstation .env never ships)" (@($r | Where-Object { $_ -like '*AIAssisted/.env differs*' }).Count -eq 1) ($r -join "; ")

    $p = New-Package "dot"; Set-Content (Join-Path $p 'AIAssisted/backend/.npmrc') "//registry/:_authToken=x"
    $r = Run $p
    Check "an unknown hidden file fails, by path only" (@($r | Where-Object { $_ -eq 'hidden entry not on the allowlist: AIAssisted/backend/.npmrc' }).Count -eq 1) ($r -join "; ")
    Check "no file content is reported" (-not ($r -join ' ').Contains('_authToken'))

    $p = New-Package "dotdir"; New-Item -ItemType Directory -Force (Join-Path $p 'AIAssisted/.vs') | Out-Null; Set-Content (Join-Path $p 'AIAssisted/.vs/x') "x"
    $r = Run $p
    Check "an unknown hidden folder fails" (@($r | Where-Object { $_ -like '*AIAssisted/.vs' }).Count -ge 1) ($r -join "; ")

    $p = New-Package "local"; Set-Content (Join-Path $p 'AIAssisted/backend/appsettings.Local.json') "{}"; Set-Content (Join-Path $p 'AIAssisted/backend/appsettings.Development.json') "{}"
    $r = Run $p
    Check "appsettings.Local.json and appsettings.Development.json fail" (@($r | Where-Object { $_ -like 'not allowed*appsettings.*' }).Count -eq 2) ($r -join "; ")

    $p = New-Package "keys"; Set-Content (Join-Path $p 'scripts/dev.pfx') "x"; Set-Content (Join-Path $p 'AIAssisted/backend/.playwright/node/k.pem') "x"; Set-Content (Join-Path $p 'secrets.json') "{}"
    $r = Run $p
    Check "certificates, keys and user-secrets fail, also inside the allowed Playwright folder" (@($r | Where-Object { $_ -like 'not allowed*' }).Count -eq 3) ($r -join "; ")

    $p = New-Package "git"; New-Item -ItemType Directory -Force (Join-Path $p '.git') | Out-Null; Set-Content (Join-Path $p '.git/config') "x"
    $r = Run $p
    Check ".git metadata fails" (@($r | Where-Object { $_ -like 'repository metadata*' }).Count -eq 1) ($r -join "; ")

    $p = New-Package "noenv"; Remove-Item -LiteralPath (Join-Path $p 'AIAssisted/.env')
    $r = Run $p
    Check "a package without .env is not this check's concern (validate-package.ps1 reports it)" ($r.Count -eq 0) ($r -join "; ")

    $p = New-Package "exit"; $hostExe = (Get-Process -Id $PID).Path
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $check -PackageRoot $p -EnvTemplate $template | Out-Null
    $okExit = $LASTEXITCODE
    Set-Content (Join-Path $p 'AIAssisted/.secret') "x"
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $check -PackageRoot $p -EnvTemplate $template | Out-Null
    Check "exit code 0 when clean, 1 when not (CI gate)" ($okExit -eq 0 -and $LASTEXITCODE -eq 1) "$okExit/$LASTEXITCODE"
}
finally { Remove-Item -LiteralPath $base -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue }
exit $script:failures
