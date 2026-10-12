<#
.SYNOPSIS
  Builds src/Cleanup.App/Assets/Cleanup.ico from the macOS 1024 px app icon (design §8): white sparkles on the
  red → orange gradient. Entries below 256 px are 32-bit DIBs (what every icon reader understands), 256 px is PNG.
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\src\Cleanup.App\Assets\AppIcon.png'),
    [string]$Target = (Join-Path $PSScriptRoot '..\src\Cleanup.App\Assets\Cleanup.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$image = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
# The macOS icon has a transparent margin and drop shadow; Windows icons fill their square, so crop to the tile.
$crop = New-Object System.Drawing.Rectangle 96, 92, 836, 836

function Render([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($image, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), $crop, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    return $bitmap
}

function Dib([System.Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER: height counts the XOR image and the AND mask.
    $w.Write([UInt32]40); $w.Write([Int32]$size); $w.Write([Int32]($size * 2)); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]0); $w.Write([UInt32]0); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bitmap.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    # AND mask: all zero (alpha does the work), rows padded to 32 bits.
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $size)))
    $w.Flush()
    return , $ms.ToArray()
}

$entries = foreach ($size in $sizes) {
    $bitmap = Render $size
    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    } else {
        , (Dib $bitmap)
    }
    $bitmap.Dispose()
}
$image.Dispose()

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$entries[$i].Length); $w.Write([UInt32]$offset)
    $offset += $entries[$i].Length
}
foreach ($entry in $entries) { $w.Write($entry) }
$w.Flush()
$full = Join-Path (Resolve-Path (Split-Path $Target)).Path (Split-Path $Target -Leaf)
[IO.File]::WriteAllBytes($full, $out.ToArray())
Write-Host "Wrote $full"
