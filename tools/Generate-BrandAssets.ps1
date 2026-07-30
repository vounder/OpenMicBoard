[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $root 'src\OpenMicBoard.App\Assets'

function New-MarkBitmap {
    param(
        [int] $Width,
        [int] $Height,
        [double] $Scale = 0.68
    )

    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $size = [Math]::Min($Width, $Height) * $Scale
    $left = ($Width - $size) / 2
    $top = ($Height - $size) / 2
    $radius = $size * 0.23
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($left, $top, $diameter, $diameter, 180, 90)
    $path.AddArc($left + $size - $diameter, $top, $diameter, $diameter, 270, 90)
    $path.AddArc($left + $size - $diameter, $top + $size - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($left, $top + $size - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $background = [System.Drawing.SolidBrush]::new(
        [System.Drawing.Color]::FromArgb(255, 16, 19, 21))
    $graphics.FillPath($background, $path)

    $points = @(
        [System.Drawing.PointF]::new($left + ($size * 0.20), $top + ($size * 0.53)),
        [System.Drawing.PointF]::new($left + ($size * 0.29), $top + ($size * 0.53)),
        [System.Drawing.PointF]::new($left + ($size * 0.35), $top + ($size * 0.38)),
        [System.Drawing.PointF]::new($left + ($size * 0.43), $top + ($size * 0.68)),
        [System.Drawing.PointF]::new($left + ($size * 0.52), $top + ($size * 0.27)),
        [System.Drawing.PointF]::new($left + ($size * 0.61), $top + ($size * 0.61)),
        [System.Drawing.PointF]::new($left + ($size * 0.67), $top + ($size * 0.43)),
        [System.Drawing.PointF]::new($left + ($size * 0.73), $top + ($size * 0.53)),
        [System.Drawing.PointF]::new($left + ($size * 0.80), $top + ($size * 0.53))
    )
    $pen = [System.Drawing.Pen]::new(
        [System.Drawing.Color]::FromArgb(255, 159, 231, 215),
        [Math]::Max(2, $size * 0.066))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawLines($pen, $points)

    $pen.Dispose()
    $background.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function Save-Png {
    param([string] $Name, [int] $Width, [int] $Height, [double] $Scale = 0.68)
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

Save-Png 'Square150x150Logo.scale-200.png' 300 300 0.82
Save-Png 'Square44x44Logo.scale-200.png' 88 88 0.90
Save-Png 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24 1.00
Save-Png 'Square44x44Logo.targetsize-48_altform-lightunplated.png' 48 48 0.96
Save-Png 'StoreLogo.png' 50 50 0.90
Save-Png 'LockScreenLogo.scale-200.png' 48 48 0.90
Save-Png 'Wide310x150Logo.scale-200.png' 620 300 0.70
Save-Png 'SplashScreen.scale-200.png' 1240 600 0.48
Save-Png 'BrandMark.png' 1024 1024 0.86

$iconBitmap = New-MarkBitmap -Width 256 -Height 256 -Scale 0.90
try {
    $handle = $iconBitmap.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($handle)
    $stream = [System.IO.File]::Create((Join-Path $assetDirectory 'AppIcon.ico'))
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

Write-Host "Generated brand assets in $assetDirectory"
