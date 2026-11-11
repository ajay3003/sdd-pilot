# BirkNext build identity: one resolution per pipeline run (scripts/resolve-build-identity.ps1), a build manifest for the package
# (scripts/write-build-manifest.ps1) and its verification (scripts/validate-package.ps1). Runs on Windows PowerShell 5.1 and pwsh.
# Policy and fields: docs/architecture/birknext-build-version-model.md. Nothing here invents a CI value: an absent value stays $null.

$script:SemVerCore = '^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$'
$script:IdentitySchema = 'birknext-build-identity/1'
$script:ManifestSchema = 'birknext-build-manifest/1'

function Get-BirkNextApplicationVersion([string]$PropsPath) {
    if (-not (Test-Path -LiteralPath $PropsPath)) { throw "Version file not found: $PropsPath" }
    [xml]$xml = Get-Content -LiteralPath $PropsPath -Raw
    $value = @($xml.Project.PropertyGroup | ForEach-Object { $_.BirkNextApplicationVersion } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $value) { throw "BirkNextApplicationVersion is not set in $PropsPath" }
    return ([string]$value).Trim()
}

function Test-BirkNextSemVerCore([string]$Version) { return [bool]($Version -match $script:SemVerCore) }

# A branch name comes from whoever pushed it: keep only a safe character set and a bounded length, never pass it to a shell.
function ConvertTo-BirkNextSafeBranch([string]$Branch) {
    if ([string]::IsNullOrWhiteSpace($Branch)) { return $null }
    $b = $Branch.Trim() -replace '^refs/heads/', '' -replace '^refs/', ''
    $b = $b -replace '[^A-Za-z0-9._/-]', '-'
    if ($b.Length -gt 200) { $b = $b.Substring(0, 200) }
    return $b
}

<#
Resolves the build identity.
- Env: a hashtable of the pipeline variables as environment variables (TF_BUILD, BUILD_BUILDID, BUILD_BUILDNUMBER, BUILD_SOURCEVERSION,
  BUILD_SOURCEBRANCH, BUILD_DEFINITIONNAME). TF_BUILD=True means an Azure DevOps run.
- Git: a scriptblock returning @{ Commit; Dirty; Branch } for the working tree (local builds only); $null when there is no git.
- Release: an official release build (requires a complete CI identity).
Returns [pscustomobject] with Ok, Problems, Warnings and the identity fields.
#>
function Resolve-BirkNextBuildIdentity {
    param([string]$ApplicationVersion, [hashtable]$Env, [scriptblock]$Git, [switch]$Release, [string]$Configuration = 'Release', [DateTime]$NowUtc = [DateTime]::UtcNow)
    $problems = New-Object System.Collections.Generic.List[string]
    $warnings = New-Object System.Collections.Generic.List[string]
    if (-not (Test-BirkNextSemVerCore $ApplicationVersion)) { $problems.Add("Application version '$ApplicationVersion' is not MAJOR.MINOR.PATCH.") }
    $ci = "$($Env['TF_BUILD'])" -eq 'True'
    $id = [ordered]@{
        schema = $script:IdentitySchema; product = 'BirkNext'; applicationVersion = $ApplicationVersion; buildVersion = $null; channel = $null
        buildId = $null; buildNumber = $null; fileRevision = 0; sourceCommit = $null; sourceBranch = $null; sourceDirty = $null
        buildTimestampUtc = $null; configuration = $Configuration; pipelineProvider = $null; pipelineDefinition = $null; provenance = $null
    }
    if ($ci) {
        $id.pipelineProvider = 'AzureDevOps'
        $buildId = "$($Env['BUILD_BUILDID'])".Trim()
        if ($buildId -match '^[0-9]{1,12}$') { $id.buildId = $buildId; $id.fileRevision = [int]([long]$buildId % 65535) }
        else { $problems.Add("BUILD_BUILDID is missing or not numeric ('$buildId'): a CI build needs its unique run id.") }
        $commit = "$($Env['BUILD_SOURCEVERSION'])".Trim().ToLowerInvariant()
        if ($commit -match '^[0-9a-f]{40}$') { $id.sourceCommit = $commit }
        else { $problems.Add("BUILD_SOURCEVERSION is missing or not a 40-character commit SHA.") }
        $number = "$($Env['BUILD_BUILDNUMBER'])".Trim()
        if ($number -match '^[A-Za-z0-9._-]{1,100}$') { $id.buildNumber = $number }
        elseif ($number) { $warnings.Add("BUILD_BUILDNUMBER '$number' has unsupported characters and is not recorded (the build id identifies the run).") }
        $id.sourceBranch = ConvertTo-BirkNextSafeBranch "$($Env['BUILD_SOURCEBRANCH'])"
        $definition = "$($Env['BUILD_DEFINITIONNAME'])".Trim() -replace '[^A-Za-z0-9 ._-]', '-'
        if ($definition) { $id.pipelineDefinition = $definition.Substring(0, [Math]::Min(100, $definition.Length)) }
        $id.sourceDirty = $false   # a pipeline checkout is the commit it names
        $id.buildTimestampUtc = $NowUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
        $id.channel = if ($Release) { 'Release' } else { 'CI' }
        $id.buildVersion = if ($Release) { $ApplicationVersion } else { "$ApplicationVersion-ci.$($id.buildId)" }
        $id.provenance = 'Complete'
    }
    else {
        if ($Release) { $problems.Add('An official release build needs Azure DevOps provenance (TF_BUILD, BUILD_BUILDID, BUILD_SOURCEVERSION); a local build is never a release.') }
        foreach ($k in 'BUILD_BUILDID', 'BUILD_BUILDNUMBER', 'BUILD_SOURCEVERSION') {
            if ($Env[$k]) { $warnings.Add("$k is set but TF_BUILD is not: treated as a local build, the CI value is ignored.") }
        }
        $g = if ($Git) { & $Git } else { $null }
        if ($g -and "$($g.Commit)" -match '^[0-9a-fA-F]{40}$') {
            $id.sourceCommit = "$($g.Commit)".ToLowerInvariant()
            $id.sourceDirty = [bool]$g.Dirty
            $id.sourceBranch = ConvertTo-BirkNextSafeBranch "$($g.Branch)"
            $id.provenance = 'Partial'
            if ($g.Dirty) { $warnings.Add('The working tree has uncommitted changes: the binaries may not match the commit.') }
        }
        else {
            $id.sourceCommit = 'unknown'
            $id.provenance = 'Unknown'
            $warnings.Add('No git metadata: the source commit is unknown.')
        }
        $id.channel = 'Local'
        $id.buildVersion = "$ApplicationVersion-local"
    }
    if ($Release -and $id.sourceDirty) { $problems.Add('A release build must come from a clean checkout.') }
    return [pscustomobject]@{ Ok = ($problems.Count -eq 0); Problems = @($problems); Warnings = @($warnings); Identity = [pscustomobject]$id }
}

function Get-BirkNextGitState([string]$Directory) {
    $commit = (& git -C $Directory rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $commit) { return $null }
    $status = (& git -C $Directory status --porcelain 2>$null)
    $branch = (& git -C $Directory rev-parse --abbrev-ref HEAD 2>$null)
    return @{ Commit = "$commit".Trim(); Dirty = [bool]($status); Branch = "$branch".Trim() }
}

function ConvertTo-BirkNextXml([object]$Value) { if ($null -eq $Value) { return '' } return [System.Security.SecurityElement]::Escape([string]$Value) }

# Writes build-identity.json and build-identity.props (the MSBuild input for every project of this run) to $OutDir.
function Write-BirkNextBuildIdentity($Identity, [string]$OutDir) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $json = $Identity | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText((Join-Path $OutDir 'build-identity.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
    $dirty = if ($null -eq $Identity.sourceDirty) { 'unknown' } elseif ($Identity.sourceDirty) { 'true' } else { 'false' }
    $props = @"
<Project>
  <!-- Generated by scripts/resolve-build-identity.ps1 for one build. Do not edit or commit. -->
  <PropertyGroup>
    <BirkNextBuildChannel>$(ConvertTo-BirkNextXml $Identity.channel)</BirkNextBuildChannel>
    <BirkNextBuildVersion>$(ConvertTo-BirkNextXml $Identity.buildVersion)</BirkNextBuildVersion>
    <BirkNextBuildId>$(ConvertTo-BirkNextXml $Identity.buildId)</BirkNextBuildId>
    <BirkNextBuildNumber>$(ConvertTo-BirkNextXml $Identity.buildNumber)</BirkNextBuildNumber>
    <BirkNextFileRevision>$(ConvertTo-BirkNextXml $Identity.fileRevision)</BirkNextFileRevision>
    <BirkNextSourceCommit>$(ConvertTo-BirkNextXml $Identity.sourceCommit)</BirkNextSourceCommit>
    <BirkNextSourceBranch>$(ConvertTo-BirkNextXml $Identity.sourceBranch)</BirkNextSourceBranch>
    <BirkNextSourceDirty>$dirty</BirkNextSourceDirty>
    <BirkNextBuildTimestamp>$(ConvertTo-BirkNextXml $Identity.buildTimestampUtc)</BirkNextBuildTimestamp>
    <BirkNextPipelineDefinition>$(ConvertTo-BirkNextXml $Identity.pipelineDefinition)</BirkNextPipelineDefinition>
  </PropertyGroup>
</Project>
"@
    [System.IO.File]::WriteAllText((Join-Path $OutDir 'build-identity.props'), $props, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-BirkNextInformationalVersion($Identity) {
    $meta = if ($Identity.sourceDirty -eq $true) { "$($Identity.sourceCommit).dirty" } else { "$($Identity.sourceCommit)" }
    return "$($Identity.buildVersion)+$meta"
}

function Get-BirkNextArtifactName($Identity) { return "BirkNext-$($Identity.buildVersion)" }

function Read-BirkNextJson([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) } catch { return $null }
}

# The informational version a .NET assembly was compiled with (its Win32/metadata product version).
function Get-BirkNextAssemblyProductVersion([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
}

# Whether a compiled file (e.g. a webcil .wasm) contains the UTF-8 bytes of $Text: custom attribute strings are stored as UTF-8.
function Test-BirkNextFileContainsText([string]$Path, [string]$Text) {
    if (-not (Test-Path -LiteralPath $Path) -or -not $Text) { return $false }
    $haystack = [System.Text.Encoding]::GetEncoding(28591).GetString([System.IO.File]::ReadAllBytes($Path))
    $needle = [System.Text.Encoding]::GetEncoding(28591).GetString([System.Text.Encoding]::UTF8.GetBytes($Text))
    return $haystack.Contains($needle)
}

# Builds the package's build-manifest.json from the resolved identity and what the package actually contains (component versions are read
# from the compiled files, not copied from the identity, so a mismatch is visible).
function New-BirkNextBuildManifest($Identity, [string]$PackageRoot) {
    $backendDll = Join-Path $PackageRoot 'AIAssisted/backend/BirkNext.Api.dll'
    $frontendInfo = Read-BirkNextJson (Join-Path $PackageRoot 'AIAssisted/frontend/wwwroot/build-info.json')
    $companion = Read-BirkNextJson (Join-Path $PackageRoot 'AIAssisted/backend/BrowserCompanion/build-info.json')
    $name = Get-BirkNextArtifactName $Identity
    return [pscustomobject][ordered]@{
        manifestSchemaVersion = $script:ManifestSchema
        product = 'BirkNext'
        applicationVersion = $Identity.applicationVersion
        buildVersion = $Identity.buildVersion
        informationalVersion = (Get-BirkNextInformationalVersion $Identity)
        channel = $Identity.channel
        pipelineProvider = $Identity.pipelineProvider
        pipelineDefinition = $Identity.pipelineDefinition
        pipelineRunId = $Identity.buildId
        buildNumber = $Identity.buildNumber
        sourceCommit = $Identity.sourceCommit
        sourceBranch = $Identity.sourceBranch
        sourceDirty = $Identity.sourceDirty
        buildTimestampUtc = $Identity.buildTimestampUtc
        buildConfiguration = $Identity.configuration
        components = [pscustomobject][ordered]@{
            backend = [pscustomobject][ordered]@{ file = 'AIAssisted/backend/BirkNext.Api.dll'; informationalVersion = (Get-BirkNextAssemblyProductVersion $backendDll) }
            frontend = [pscustomobject][ordered]@{ file = 'AIAssisted/frontend/wwwroot/build-info.json'; informationalVersion = $(if ($frontendInfo) { $frontendInfo.informationalVersion } else { $null }) }
            browserCompanion = $(if ($companion) { [pscustomobject][ordered]@{ version = $companion.version; buildId = $companion.buildId; relation = 'Bundled in the backend (BrowserCompanion/); versioned independently' } } else { $null })
        }
        artifactIdentity = [pscustomobject][ordered]@{
            name = $name
            archive = "$name.zip"
            digest = "External: $name.zip.sha256 (an archive cannot contain its own digest)"
        }
        provenanceStatus = $Identity.provenance
    }
}

# Checks a package's build provenance: the manifest against the compiled backend, the frontend metadata and the WebAssembly bytes, and the
# bundled Companion. Returns a list of problems (empty = consistent).
function Test-BirkNextBuildProvenance([string]$PackageRoot) {
    $problems = New-Object System.Collections.Generic.List[string]
    $manifest = Read-BirkNextJson (Join-Path $PackageRoot 'build-manifest.json')
    if (-not $manifest) { $problems.Add('build-manifest.json is missing or not valid JSON'); return @($problems) }
    if ($manifest.manifestSchemaVersion -ne $script:ManifestSchema) { $problems.Add("Unsupported build manifest schema '$($manifest.manifestSchemaVersion)'") }
    if (-not (Test-BirkNextSemVerCore $manifest.applicationVersion)) { $problems.Add("The manifest application version '$($manifest.applicationVersion)' is not MAJOR.MINOR.PATCH") }
    if ($manifest.channel -in @('CI', 'Release') -and (-not $manifest.pipelineRunId -or "$($manifest.sourceCommit)" -notmatch '^[0-9a-f]{40}$')) {
        $problems.Add("A $($manifest.channel) manifest without a pipeline run id and a full source commit")
    }
    $expected = $manifest.informationalVersion
    $backend = Get-BirkNextAssemblyProductVersion (Join-Path $PackageRoot 'AIAssisted/backend/BirkNext.Api.dll')
    if ($backend -ne $expected) { $problems.Add("Backend BirkNext.Api.dll is '$backend', the manifest says '$expected'") }
    $frontendInfo = Read-BirkNextJson (Join-Path $PackageRoot 'AIAssisted/frontend/wwwroot/build-info.json')
    if (-not $frontendInfo) { $problems.Add('Frontend wwwroot/build-info.json is missing') }
    elseif ($frontendInfo.informationalVersion -ne $expected) { $problems.Add("Frontend build-info.json is '$($frontendInfo.informationalVersion)', the manifest says '$expected'") }
    $wasm = Get-ChildItem -Path (Join-Path $PackageRoot 'AIAssisted/frontend/wwwroot/_framework') -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('BirkNext.Web.wasm', 'BirkNext.Web.dll') } | Select-Object -First 1
    if (-not $wasm) { $problems.Add('The compiled frontend (BirkNext.Web.wasm) is missing') }
    elseif (-not (Test-BirkNextFileContainsText $wasm.FullName $expected)) { $problems.Add("The compiled frontend ($($wasm.Name)) does not carry '$expected'") }
    if ($manifest.components.backend.informationalVersion -ne $backend) { $problems.Add('The manifest backend component version differs from BirkNext.Api.dll') }
    $companion = Read-BirkNextJson (Join-Path $PackageRoot 'AIAssisted/backend/BrowserCompanion/build-info.json')
    if ($manifest.components.browserCompanion -and (-not $companion -or $companion.buildId -ne $manifest.components.browserCompanion.buildId -or $companion.version -ne $manifest.components.browserCompanion.version)) {
        $problems.Add('The bundled Browser Companion differs from the manifest')
    }
    return @($problems)
}

Export-ModuleMember -Function Get-BirkNextApplicationVersion, Test-BirkNextSemVerCore, ConvertTo-BirkNextSafeBranch, Resolve-BirkNextBuildIdentity, Get-BirkNextGitState,
    Write-BirkNextBuildIdentity, Get-BirkNextInformationalVersion, Get-BirkNextArtifactName, Read-BirkNextJson, Get-BirkNextAssemblyProductVersion,
    Test-BirkNextFileContainsText, New-BirkNextBuildManifest, Test-BirkNextBuildProvenance
