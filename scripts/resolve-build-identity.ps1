<#
.SYNOPSIS
Resolve the BirkNext build identity ONCE for a build and write it for every later step.

.DESCRIPTION
Reads the application version from AIAssisted/versioning/BirkNext.Version.props (the only version source) and, in Azure DevOps (TF_BUILD=True),
the run's BUILD_BUILDID, BUILD_BUILDNUMBER, BUILD_SOURCEVERSION, BUILD_SOURCEBRANCH and BUILD_DEFINITIONNAME; elsewhere the working tree's
git commit and dirty state (channel Local). Writes <OutDir>/build-identity.json and <OutDir>/build-identity.props; every dotnet build or
publish of the same run passes -p:BirkNextBuildIdentityProps=<OutDir>/build-identity.props, so nothing is recomputed per project.
In Azure DevOps it also sets the pipeline variables BirkNextBuildVersion and BirkNextArtifactName (names are fixed; values are validated).

-Release marks an official release build: it requires a complete CI identity from a clean checkout and fails otherwise.
Exit code 0 = resolved; 1 = provenance incomplete or invalid (message says why); 2 = script error.
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [switch]$Release,
    [string]$Configuration = 'Release',
    [string]$RepositoryRoot = ''
)
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'BirkNextBuildIdentity.psm1') -Force
    if (-not $RepositoryRoot) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }
    $version = Get-BirkNextApplicationVersion (Join-Path $RepositoryRoot 'AIAssisted/versioning/BirkNext.Version.props')
    $envVars = @{}
    foreach ($k in 'TF_BUILD', 'BUILD_BUILDID', 'BUILD_BUILDNUMBER', 'BUILD_SOURCEVERSION', 'BUILD_SOURCEBRANCH', 'BUILD_DEFINITIONNAME') {
        $envVars[$k] = [Environment]::GetEnvironmentVariable($k)
    }
    $root = $RepositoryRoot
    $result = Resolve-BirkNextBuildIdentity -ApplicationVersion $version -Env $envVars -Git { Get-BirkNextGitState $root }.GetNewClosure() -Release:$Release -Configuration $Configuration
    foreach ($w in $result.Warnings) { Write-Host "[WARN] $w" -ForegroundColor Yellow }
    if (-not $result.Ok) {
        foreach ($p in $result.Problems) { Write-Host "[FAIL] $p" -ForegroundColor Red }
        exit 1
    }
    Write-BirkNextBuildIdentity $result.Identity $OutDir
    $id = $result.Identity
    Write-Host "[OK] BirkNext $($id.buildVersion) ($($id.channel)) commit $($id.sourceCommit) -> $OutDir" -ForegroundColor Green
    if ($envVars['TF_BUILD'] -eq 'True') {
        Write-Host "##vso[task.setvariable variable=BirkNextBuildVersion]$($id.buildVersion)"
        Write-Host "##vso[task.setvariable variable=BirkNextArtifactName]$(Get-BirkNextArtifactName $id)"
    }
    exit 0
}
catch {
    Write-Host "[ERROR] Build identity could not be resolved: $_" -ForegroundColor Red
    exit 2
}
