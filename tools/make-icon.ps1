# 生成应用图标 assets/app.ico（PNG 压缩 ICO，256×256）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 56
$d = $r * 2
$path.AddArc(0, 0, $d, $d, 180, 90)
$path.AddArc($size - $d, 0, $d, $d, 270, 90)
$path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
$path.AddArc(0, $size - $d, $d, $d, 90, 90)
$path.CloseFigure()

$rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect,
    [System.Drawing.Color]::FromArgb(255, 78, 118, 255),
    [System.Drawing.Color]::FromArgb(255, 138, 78, 255),
    45)
$g.FillPath($brush, $path)

$brush2 = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.RectangleF(0, 0, $size, $size * 0.55)),
    [System.Drawing.Color]::FromArgb(60, 255, 255, 255),
    [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
    90)
$g.FillPath($brush2, $path)

$dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
$cell = 34
$gap = 16
$start = ($size - (3 * $cell + 2 * $gap)) / 2
for ($row = 0; $row -lt 3; $row++) {
    for ($col = 0; $col -lt 3; $col++) {
        $x = $start + $col * ($cell + $gap)
        $y = $start + $row * ($cell + $gap)
        $g.FillEllipse($dotBrush, (New-Object System.Drawing.RectangleF($x, $y, $cell, $cell)))
    }
}
$g.Dispose()

$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$ms.Dispose()
$bmp.Dispose()

$outDir = Join-Path $PSScriptRoot '..\assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$icoPath = Join-Path $outDir 'app.ico'

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)          # reserved
$bw.Write([UInt16]1)          # type = icon
$bw.Write([UInt16]1)          # count
$bw.Write([Byte]0)            # width 0 => 256
$bw.Write([Byte]0)            # height 0 => 256
$bw.Write([Byte]0)            # colors
$bw.Write([Byte]0)            # reserved
$bw.Write([UInt16]1)          # planes
$bw.Write([UInt16]32)         # bpp
$bw.Write([UInt32]$png.Length)
$bw.Write([UInt32]22)         # offset
$bw.Write($png)
$bw.Flush()
$bw.Close()
$fs.Close()

Write-Output ("icon: " + $icoPath + " (" + $png.Length + " bytes png)")
