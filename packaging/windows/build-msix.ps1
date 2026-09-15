# Builds the MSIX bundle for KT Markdown Viewer.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File packaging/windows/build-msix.ps1
#
# Output: artifacts/msix/<name>_<version>_Test/<name>_<version>_x64.appxbundle
# The bundle is unsigned. Install it during development with
# Add-AppxPackage -Register on the loose files (see README.md).

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
$project = Join-Path $repoRoot 'src\MISX-Install\MISX-Install.wapproj'

$msbuildCommand = Get-Command msbuild -ErrorAction SilentlyContinue
$msbuild = if ($msbuildCommand) { $msbuildCommand.Source } else { $null }
if (-not $msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'MSBuild not found. Run from a Developer PowerShell prompt.' }
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'MSBuild not found. Run from a Developer PowerShell prompt.' }
}

$arguments = @(
    $project
    '/restore'
    "/p:Configuration=$Configuration"
    "/p:Platform=$Platform"
    "/p:AppxBundlePlatforms=$Platform"
    '/v:minimal'
    '/nologo'
)

Write-Host "Building MSIX ($Configuration|$Platform)…"
& $msbuild @arguments
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE." }

$output = Join-Path $repoRoot 'artifacts\msix'
Get-ChildItem -Path $output -Recurse -Include *.appxbundle, *.msixbundle -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "  $($_.FullName)" }
