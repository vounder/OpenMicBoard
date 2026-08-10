[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $root 'src\OpenMicBoard.App\Assets'
$masterPath = Join-Path $assetDirectory 'BrandMark-v2.png'
if (-not (Test-Path -LiteralPath $masterPath -PathType Leaf)) {
    throw "Brand master not found: $masterPath"
}

$master = [System.Drawing.Bitmap]::FromFile($masterPath)

function New-MarkBitmap {
    param(
        [int] $Width,
        [int] $Height,
        [double] $Scale = 0.86
    )

    $bitmap = [System.Drawing.Bitmap]::new(
        $Width,
        $Height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

        $size = [Math]::Min($Width, $Height) * $Scale
        $left = ($Width - $size) / 2
        $top = ($Height - $size) / 2
        $destination = [System.Drawing.RectangleF]::new($left, $top, $size, $size)
        $graphics.DrawImage($master, $destination)
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Save-Png {
    param(
        [string] $Name,
        [int] $Width,
        [int] $Height,
        [double] $Scale = 0.86
    )

    $bitmap = New-MarkBitmap -Width $Width -Height $Height -Scale $Scale
    try {
        $bitmap.Save(
            (Join-Path $assetDirectory $Name),
            [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

try {
    Save-Png 'Square150x150Logo-v2.scale-200.png' 300 300 1.00
    Save-Png 'Square44x44Logo-v2.scale-200.png' 88 88 1.00
    Save-Png 'Square44x44Logo-v2.targetsize-24_altform-unplated.png' 24 24 1.12
    Save-Png 'Square44x44Logo-v2.targetsize-48_altform-lightunplated.png' 48 48 1.06
    Save-Png 'StoreLogo-v2.png' 50 50 1.04
    Save-Png 'LockScreenLogo-v2.scale-200.png' 48 48 1.06
    Save-Png 'Wide310x150Logo-v2.scale-200.png' 620 300 0.96
    Save-Png 'SplashScreen-v2.scale-200.png' 1240 600 0.76

    $iconBitmap = New-MarkBitmap -Width 256 -Height 256 -Scale 1.04
    try {
        $handle = $iconBitmap.GetHicon()
        $icon = [System.Drawing.Icon]::FromHandle($handle)
        $stream = [System.IO.File]::Create((Join-Path $assetDirectory 'AppIcon-v2.ico'))
        try {
            $icon.Save($stream)
        }
        finally {
            $stream.Dispose()
            $icon.Dispose()
        }
    }
    finally {
        $iconBitmap.Dispose()
    }
}
finally {
    $master.Dispose()
}

Write-Host "Generated OpenMicBoard v2 brand assets from $masterPath"
