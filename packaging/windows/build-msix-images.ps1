# Builds the MSIX tile/logo assets for src/MISX-Install/Images from the
# 512x512 master PNG produced by design/icon/build-icons.py.
#
# Run from anywhere:  pwsh -File packaging/windows/build-msix-images.ps1

[CmdletBinding()]
param(
    [string]$Source,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Source) { $Source = Join-Path $scriptRoot '..\linux\hicolor\512x512\apps\mdviewer.png' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $scriptRoot '..\..\src\MISX-Install\Images' }

$Source = (Resolve-Path $Source).Path
if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

# name = canvas width, canvas height. The glyph is drawn square and centred so
# the wide tiles and the splash screen keep the icon's aspect ratio.
$assets = @(
    @{ Name = 'StoreLogo.png';                       Width = 50;   Height = 50 }
    @{ Name = 'Square44x44Logo.png';                  Width = 44;   Height = 44 }
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; Width = 24; Height = 24 }
    @{ Name = 'Square71x71Logo.png';                  Width = 71;   Height = 71 }
    @{ Name = 'Square150x150Logo.png';                Width = 150;  Height = 150 }
    @{ Name = 'Square310x310Logo.png';                Width = 310;  Height = 310 }
    @{ Name = 'Wide310x150Logo.png';                  Width = 310;  Height = 150 }
    @{ Name = 'SplashScreen.png';                     Width = 620;  Height = 300 }
    @{ Name = 'LockScreenLogo.png';                   Width = 24;   Height = 24 }
)

$master = [System.Drawing.Image]::FromFile($Source)
try {
    foreach ($asset in $assets) {
        $bitmap = New-Object System.Drawing.Bitmap($asset.Width, $asset.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

            $glyph = [Math]::Min($asset.Width, $asset.Height)
            $x = [int](($asset.Width - $glyph) / 2)
            $y = [int](($asset.Height - $glyph) / 2)
            $graphics.DrawImage($master, $x, $y, $glyph, $glyph)
        }
        finally {
            $graphics.Dispose()
        }

        $target = Join-Path $OutputDirectory $asset.Name
        $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        Write-Host ("  {0}  ({1}x{2})" -f $asset.Name, $asset.Width, $asset.Height)
    }
}
finally {
    $master.Dispose()
}

Write-Host "MSIX images written to $OutputDirectory"
