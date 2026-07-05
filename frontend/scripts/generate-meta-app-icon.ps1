param(
    [string]$Source = "frontend/public/branding/icon-transparent.png",
    [string]$Output = "frontend/public/branding/meta-app-icon.png",
    [int]$CanvasSize = 1024,
    [int]$SafeMaxDimension = 760
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

function Get-AlphaBounds {
    param([System.Drawing.Bitmap]$Bitmap)

    $minX = $Bitmap.Width
    $minY = $Bitmap.Height
    $maxX = -1
    $maxY = -1

    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -gt 0) {
                if ($x -lt $minX) { $minX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }

    if ($maxX -lt 0 -or $maxY -lt 0) {
        throw "Source image has no non-transparent pixels."
    }

    return [System.Drawing.Rectangle]::FromLTRB($minX, $minY, $maxX + 1, $maxY + 1)
}

$sourcePath = (Resolve-Path $Source).Path
$outputPath = Join-Path (Get-Location) $Output
$outputDir = Split-Path -Parent $outputPath
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

$sourceBitmap = [System.Drawing.Bitmap]::FromFile($sourcePath)
try {
    $bounds = Get-AlphaBounds -Bitmap $sourceBitmap
    $contentWidth = $bounds.Width
    $contentHeight = $bounds.Height
    $scale = [Math]::Min($SafeMaxDimension / $contentWidth, $SafeMaxDimension / $contentHeight)
    $targetWidth = [Math]::Round($contentWidth * $scale)
    $targetHeight = [Math]::Round($contentHeight * $scale)
    $offsetX = [Math]::Floor(($CanvasSize - $targetWidth) / 2)
    $offsetY = [Math]::Floor(($CanvasSize - $targetHeight) / 2)

    $outputBitmap = New-Object System.Drawing.Bitmap($CanvasSize, $CanvasSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($outputBitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

            $destRect = New-Object System.Drawing.Rectangle($offsetX, $offsetY, $targetWidth, $targetHeight)
            $graphics.DrawImage($sourceBitmap, $destRect, $bounds, [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally {
            $graphics.Dispose()
        }

        if (Test-Path $outputPath) {
            Remove-Item -LiteralPath $outputPath
        }
        $outputBitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $outputBitmap.Dispose()
    }

    Write-Output "Created $Output from $Source"
    Write-Output "Alpha bounds: $($bounds.X),$($bounds.Y) to $($bounds.Right - 1),$($bounds.Bottom - 1)"
    Write-Output "Placed content at ${targetWidth}x${targetHeight} with offsets ${offsetX},${offsetY}"
}
finally {
    $sourceBitmap.Dispose()
}
