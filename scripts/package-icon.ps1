param([Parameter(Mandatory=$true)][string]$Source, [Parameter(Mandatory=$true)][string]$SmallSource)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDir = Join-Path $PSScriptRoot '../src/VectorForge/Assets'
[System.IO.Directory]::CreateDirectory($assetDir) | Out-Null
$original = [System.Drawing.Image]::FromFile($Source)
$smallOriginal = [System.Drawing.Image]::FromFile($SmallSource)
function Resize-Icon([int]$size, [switch]$Simplified) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $selected = if ($size -le 32 -or $Simplified) { $smallOriginal } else { $original }
    $graphics.DrawImage($selected, 0, 0, $size, $size)
    $graphics.Dispose()
    return $bitmap
}
$master = Resize-Icon 1024
$master.Save((Join-Path $assetDir 'VectorForge.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$master.Dispose()
$smallMaster = Resize-Icon 256 -Simplified
$smallMaster.Save((Join-Path $assetDir 'VectorForge-small.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$smallMaster.Dispose()
$sizes = @(16,20,24,32,40,48,64,128,256)
$blobs = @()
foreach ($size in $sizes) {
    $bitmap = Resize-Icon $size
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += ,$stream.ToArray()
    $bitmap.Save((Join-Path $assetDir "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $stream.Dispose()
    $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $assetDir 'VectorForge.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$blobs[$i].Length); $writer.Write([uint32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($blob in $blobs) { $writer.Write([byte[]]$blob) }
$writer.Dispose(); $original.Dispose(); $smallOriginal.Dispose()
Write-Host 'PNG and nine-size ICO packaged.'
