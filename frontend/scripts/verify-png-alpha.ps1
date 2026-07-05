param(
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [int]$ExpectedWidth = 1024,
    [int]$ExpectedHeight = 1024
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$imagePath = (Resolve-Path $Path).Path
$bitmap = [System.Drawing.Bitmap]::FromFile($imagePath)

try {
    if ($bitmap.Width -ne $ExpectedWidth -or $bitmap.Height -ne $ExpectedHeight) {
        throw "Expected ${ExpectedWidth}x${ExpectedHeight}, got $($bitmap.Width)x$($bitmap.Height)."
    }

    $hasAlpha = (($bitmap.PixelFormat -band [System.Drawing.Imaging.PixelFormat]::Alpha) -ne 0) -or
        (($bitmap.PixelFormat -band [System.Drawing.Imaging.PixelFormat]::PAlpha) -ne 0) -or
        $bitmap.PixelFormat -eq [System.Drawing.Imaging.PixelFormat]::Format32bppArgb -or
        $bitmap.PixelFormat -eq [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb

    if (-not $hasAlpha) {
        throw "Image pixel format does not expose an alpha channel: $($bitmap.PixelFormat)"
    }

    $transparentCount = 0
    $opaqueCount = 0
    $minX = $bitmap.Width
    $minY = $bitmap.Height
    $maxX = -1
    $maxY = -1

    for ($y = 0; $y -lt $bitmap.Height; $y++) {
        for ($x = 0; $x -lt $bitmap.Width; $x++) {
            $alpha = $bitmap.GetPixel($x, $y).A
            if ($alpha -eq 0) {
                $transparentCount++
                continue
            }

            $opaqueCount++
            if ($x -lt $minX) { $minX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }

    if ($transparentCount -le 0) {
        throw "Image has an alpha channel but no fully transparent pixels."
    }

    if ($opaqueCount -le 0) {
        throw "Image contains no visible logo pixels."
    }

    $corners = @(
        @{ Name = "top-left"; X = 0; Y = 0 },
        @{ Name = "top-right"; X = ($bitmap.Width - 1); Y = 0 },
        @{ Name = "bottom-left"; X = 0; Y = ($bitmap.Height - 1) },
        @{ Name = "bottom-right"; X = ($bitmap.Width - 1); Y = ($bitmap.Height - 1) }
    )

    foreach ($corner in $corners) {
        $alpha = $bitmap.GetPixel($corner.X, $corner.Y).A
        if ($alpha -ne 0) {
            throw "Corner $($corner.Name) is not transparent (alpha=$alpha)."
        }
    }

    if ($minX -le 0 -or $minY -le 0 -or $maxX -ge ($bitmap.Width - 1) -or $maxY -ge ($bitmap.Height - 1)) {
        throw "Visible content touches the canvas edge: bounds=$minX,$minY to $maxX,$maxY"
    }

    $paddingCandidates = @(
        $minX,
        $minY,
        ($bitmap.Width - 1 - $maxX),
        ($bitmap.Height - 1 - $maxY)
    )
    $padding = ($paddingCandidates | Measure-Object -Minimum).Minimum

    Write-Output "Verified $Path"
    Write-Output "Size: $($bitmap.Width)x$($bitmap.Height)"
    Write-Output "Pixel format: $($bitmap.PixelFormat)"
    Write-Output "Transparent pixels: $transparentCount"
    Write-Output "Visible content bounds: $minX,$minY to $maxX,$maxY"
    Write-Output "Minimum padding: $padding px"
}
finally {
    $bitmap.Dispose()
}
