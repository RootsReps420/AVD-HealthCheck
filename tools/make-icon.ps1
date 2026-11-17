# Rebuilds assets/app.ico from tools/icon.html (white Lloyds tile, as in Incident Lab).
# Needs Microsoft Edge. Only run this when the icon design changes; app.ico is committed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
          "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found.' }

$work = Join-Path $env:TEMP ('avdhc-icon-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$shot = Join-Path $work 'tile.png'
$page = 'file:///' + ((Join-Path $PSScriptRoot 'icon.html') -replace '\\', '/')

& $edge --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 `
        --window-size=600,600 --screenshot="$shot" $page 2>$null | Out-Null
Start-Sleep -Milliseconds 500
if (-not (Test-Path $shot)) { throw 'Edge did not produce a screenshot.' }

$source = [System.Drawing.Bitmap]::FromFile($shot)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($source, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), (New-Object System.Drawing.Rectangle 0, 0, 512, 512), [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
}
$source.Dispose()

# ICO container with PNG-compressed entries (supported since Windows Vista).
$out = Join-Path $root 'assets\app.ico'
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$img.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write($img.Bytes) }
$w.Close()

Remove-Item -Recurse -Force $work
Write-Host "Wrote $out ($((Get-Item $out).Length) bytes, sizes: $($sizes -join ', '))"
