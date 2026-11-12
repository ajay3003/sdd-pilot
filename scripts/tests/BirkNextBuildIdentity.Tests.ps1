# Self-contained tests for scripts/BirkNextBuildIdentity.psm1 (no Pester dependency: Windows PowerShell 5.1 and pwsh on CI agents).
# Synthetic pipeline variables and a simulated git only — no Azure DevOps, no network. Exit code = number of failures.
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\BirkNextBuildIdentity.psm1") -Force

$script:failures = 0
function Check([string]$name, [bool]$condition, [string]$detail = "") {
    if ($condition) { Write-Host "PASS $name" -ForegroundColor Green }
    else { $script:failures++; Write-Host "FAIL $name $detail" -ForegroundColor Red }
}

$sha = "0123456789abcdef0123456789abcdef01234567"
$ci = @{ TF_BUILD = "True"; BUILD_BUILDID = "152"; BUILD_BUILDNUMBER = "20261111.1"; BUILD_SOURCEVERSION = $sha.ToUpperInvariant(); BUILD_SOURCEBRANCH = "refs/heads/feature-test"; BUILD_DEFINITIONNAME = "qa-review-studio-ci" }
$noGit = { $null }
$cleanGit = { @{ Commit = $sha; Dirty = $false; Branch = "008-traceability-first" } }
$dirtyGit = { @{ Commit = $sha; Dirty = $true; Branch = "HEAD" } }

# ── The one version source ───────────────────────────────────────────────────────────────────────────────────
$props = Join-Path $PSScriptRoot "..\..\AIAssisted\versioning\BirkNext.Version.props"
$expected = [regex]::Match((Get-Content $props -Raw), '<BirkNextApplicationVersion>([^<]+)</BirkNextApplicationVersion>').Groups[1].Value
Check "the application version is read from versioning/BirkNext.Version.props" ((Get-BirkNextApplicationVersion $props) -eq $expected -and $expected) $expected
Check "the checked-in application version is MAJOR.MINOR.PATCH" (Test-BirkNextSemVerCore $expected) $expected
Check "SemVer core: 1.4.0 valid; 1.4, 1.4.0-rc.1, 01.2.3, v1.2.3 invalid" ((Test-BirkNextSemVerCore "1.4.0") -and -not (Test-BirkNextSemVerCore "1.4") -and -not (Test-BirkNextSemVerCore "1.4.0-rc.1") -and -not (Test-BirkNextSemVerCore "01.2.3") -and -not (Test-BirkNextSemVerCore "v1.2.3"))

# ── CI ───────────────────────────────────────────────────────────────────────────────────────────────────────
$r = Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env $ci -Git $noGit -NowUtc ([DateTime]::new(2026, 11, 11, 9, 30, 0, [DateTimeKind]::Utc))
$i = $r.Identity
Check "CI: resolved" $r.Ok ($r.Problems -join "; ")
Check "CI: build version 1.4.0-ci.152, channel CI, provenance Complete" ($i.buildVersion -eq "1.4.0-ci.152" -and $i.channel -eq "CI" -and $i.provenance -eq "Complete") "$($i.buildVersion) $($i.channel)"
Check "CI: run id, build number, lower-case commit, branch without refs/heads, definition" ($i.buildId -eq "152" -and $i.buildNumber -eq "20261111.1" -and $i.sourceCommit -eq $sha -and $i.sourceBranch -eq "feature-test" -and $i.pipelineDefinition -eq "qa-review-studio-ci")
Check "CI: file revision is the run id (mod 65535), timestamp from the run" ($i.fileRevision -eq 152 -and $i.buildTimestampUtc -eq "2026-11-11T09:30:00Z")
Check "CI: informational version is build version + commit" ((Get-BirkNextInformationalVersion $i) -eq "1.4.0-ci.152+$sha")
Check "CI: artifact name BirkNext-1.4.0-ci.152" ((Get-BirkNextArtifactName $i) -eq "BirkNext-1.4.0-ci.152")
$ciBig = $ci.Clone(); $ciBig.BUILD_BUILDID = "65537"
Check "CI: a run id above 65535 still gives a valid file revision" ((Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env $ciBig -Git $noGit).Identity.fileRevision -eq 2)

$missing = $ci.Clone(); $missing.Remove("BUILD_BUILDID")
Check "CI without BUILD_BUILDID: refused, never invented" (-not (Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env $missing -Git $cleanGit).Ok)
$badSha = $ci.Clone(); $badSha.BUILD_SOURCEVERSION = "abc123"
Check "CI with a short commit: refused" (-not (Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env $badSha -Git $cleanGit).Ok)
$evil = $ci.Clone(); $evil.BUILD_SOURCEBRANCH = 'refs/heads/x;rm -rf $(id)`"&<>'
$b = (Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env $evil -Git $noGit).Identity.sourceBranch
Check "a malicious branch name keeps only safe characters" ($b -match '^[A-Za-z0-9._/-]+$' -and $b.StartsWith("x-rm")) $b
$num = $ci.Clone(); $num.BUILD_BUILDNUMBER = 'build $(x)'
$rn = Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env $num -Git $noGit
Check "an unsafe build number is not recorded (warning), the run id still identifies the build" ($rn.Ok -and $null -eq $rn.Identity.buildNumber -and $rn.Warnings.Count -ge 1)
Check "invalid application version: refused" (-not (Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4" -Env $ci -Git $noGit).Ok)

$rel = Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env $ci -Git $noGit -Release
Check "CI release: channel Release, build version is the application version" ($rel.Ok -and $rel.Identity.channel -eq "Release" -and $rel.Identity.buildVersion -eq "1.4.0")

# ── Local ────────────────────────────────────────────────────────────────────────────────────────────────────
$l = Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env @{} -Git $cleanGit
Check "local: channel Local, 1.4.0-local, no build id or number, no timestamp" ($l.Ok -and $l.Identity.channel -eq "Local" -and $l.Identity.buildVersion -eq "1.4.0-local" -and $null -eq $l.Identity.buildId -and $null -eq $l.Identity.buildNumber -and $null -eq $l.Identity.buildTimestampUtc)
Check "local clean: commit recorded, provenance Partial (no pipeline)" ($l.Identity.sourceCommit -eq $sha -and $l.Identity.sourceDirty -eq $false -and $l.Identity.provenance -eq "Partial")
$d = Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env @{} -Git $dirtyGit
Check "local dirty: recorded as dirty with a warning; informational version says so" ($d.Identity.sourceDirty -eq $true -and $d.Warnings.Count -ge 1 -and (Get-BirkNextInformationalVersion $d.Identity) -eq "1.4.0-local+$sha.dirty")
$n = Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env @{} -Git $noGit
Check "no git (source archive): commit unknown, provenance Unknown" ($n.Ok -and $n.Identity.sourceCommit -eq "unknown" -and $n.Identity.provenance -eq "Unknown")
$half = Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env @{ BUILD_BUILDID = "152" } -Git $cleanGit
Check "CI-looking variables without TF_BUILD: still Local, CI value ignored with a warning" ($half.Identity.channel -eq "Local" -and $null -eq $half.Identity.buildId -and $half.Warnings.Count -ge 1)
Check "a local build is never a release" (-not (Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env @{} -Git $cleanGit -Release).Ok)

# ── Written identity ─────────────────────────────────────────────────────────────────────────────────────────
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("bn-identity-" + [Guid]::NewGuid().ToString("N"))
try {
    $amp = $ci.Clone(); $amp.BUILD_DEFINITIONNAME = "a&b"
    $w = (Resolve-BirkNextBuildIdentity -ApplicationVersion "1.4.0" -Env $amp -Git $noGit).Identity
    Write-BirkNextBuildIdentity $w $tmp
    [xml]$p = Get-Content (Join-Path $tmp "build-identity.props") -Raw
    $g = $p.Project.PropertyGroup
    Check "build-identity.props is valid MSBuild XML with the resolved values" ($g.BirkNextBuildVersion -eq "1.4.0-ci.152" -and $g.BirkNextBuildId -eq "152" -and $g.BirkNextSourceCommit -eq $sha -and $g.BirkNextSourceDirty -eq "false" -and $g.BirkNextFileRevision -eq "152")
    $j = Get-Content (Join-Path $tmp "build-identity.json") -Raw | ConvertFrom-Json
    Check "build-identity.json carries the same identity" ($j.buildVersion -eq "1.4.0-ci.152" -and $j.schema -eq "birknext-build-identity/1")
    Check "the definition name keeps only safe characters" ($g.BirkNextPipelineDefinition -eq "a-b") $g.BirkNextPipelineDefinition
}
finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }

# ── Pipeline YAML (static): identity resolved before compilation and passed to every build/publish ──────────
$yaml = Get-Content (Join-Path $PSScriptRoot "..\..\azure-pipelines.yml") -Raw
$resolveAt = $yaml.IndexOf("resolve-build-identity.ps1")
$firstBuild = $yaml.IndexOf("dotnet build")
Check "pipeline: the identity is resolved before the first compilation" ($resolveAt -gt 0 -and $resolveAt -lt $firstBuild)
$compiles = [regex]::Matches($yaml, 'dotnet (build|publish) ').Count
$stamped = [regex]::Matches($yaml, 'BirkNextBuildIdentityProps').Count
Check "pipeline: every dotnet build/publish gets the resolved identity" ($compiles -ge 4 -and $stamped -ge $compiles) "$compiles compilations, $stamped stamped"
Check "pipeline: one tester-facing artifact; the smoke job's internal candidate is kept; the duplicate validated copy is gone" ($yaml.Contains("artifact: 'BirkNext-Tester-Windows-x64'") -and $yaml.Contains("artifact: 'birknext-tester-package-candidate'") -and -not $yaml.Contains("artifact: 'birknext-tester-package'") -and $yaml.Contains("artifact: 'birknext-developer-package-archive'"))
Check "pipeline: the package gets a build manifest and a checksummed versioned archive" ($yaml.Contains("write-build-manifest.ps1") -and $yaml.Contains("package-archive.ps1") -and $yaml.Contains("-RequireBuildManifest"))

if ($script:failures -gt 0) { Write-Host "$script:failures failure(s)" -ForegroundColor Red } else { Write-Host "All build identity checks passed" -ForegroundColor Green }
exit $script:failures
