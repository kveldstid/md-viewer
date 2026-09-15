# Installs the MSIX package locally without signing.
#
# Registering the loose build output skips signature validation entirely, which
# is what avoids error 0x800B010A ("publisher certificate could not be
# verified"). Requires Developer Mode to be enabled in Windows Settings.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File packaging/windows/install-dev.ps1
#   ... -Uninstall

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

# Matches Identity/@Name in src/MISX-Install/Package.appxmanifest.
$packageName = '04e03980-15ed-41a9-b810-fb1802a30df7'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
$manifest = Join-Path $repoRoot "src\MISX-Install\bin\$Platform\$Configuration\AppxManifest.xml"

$developerMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($developerMode -ne 1) {
    Write-Warning 'Developer Mode appears to be disabled. Enable it under Settings > System > For developers.'
}

# An existing registration with a different architecture or publisher blocks the
# new one with 0x80073CF3, so always remove it first.
$installed = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($installed) {
    Write-Host "Removing $($installed.PackageFullName)…"
    $installed | Remove-AppxPackage
}

if ($Uninstall) {
    Write-Host 'Uninstalled.'
    return
}

if (-not (Test-Path $manifest)) {
    throw "Build output not found: $manifest. Run packaging/windows/build-msix.ps1 first."
}

Write-Host "Registering $manifest…"
Add-AppxPackage -Register $manifest

Get-AppxPackage -Name $packageName | Select-Object PackageFullName, Version, IsDevelopmentMode
