# Рисует значок EDITH (голубой светящийся шар, как индикатор на экране) в src\Edith.App\edith.ico.
# Запускать только если нужно перерисовать значок.
param([string]$Out = "C:\EDITH\src\Edith.App\edith.ico")

Add-Type -AssemblyName System.Drawing

function New-OrbPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Тёмная подложка, чтобы шар был виден и на светлой, и на тёмной панели задач.
    $pad = [Math]::Max(1, [int]($size * 0.04))
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 16, 24, 36))
    $g.FillEllipse($bg, $pad, $pad, $size - 2 * $pad, $size - 2 * $pad)

    # Свечение.
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gr = $size * 0.40
    $c = $size / 2.0
    $glowPath.AddEllipse([float]($c - $gr), [float]($c - $gr), [float](2 * $gr), [float](2 * $gr))
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
    $glow.CenterColor = [System.Drawing.Color]::FromArgb(200, 85, 223, 255)
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 85, 223, 255))
    $g.FillPath($glow, $glowPath)

    # Ядро.
    $r = $size * 0.21
    $core = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 85, 223, 255))
    $g.FillEllipse($core, [float]($c - $r), [float]($c - $r), [float](2 * $r), [float](2 * $r))
    $hr = $r * 0.45
    $hi = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 235, 252, 255))
    $g.FillEllipse($hi, [float]($c - $r * 0.55), [float]($c - $r * 0.6), [float](2 * $hr), [float](2 * $hr))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) { ,(New-OrbPng $s) }

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Close()
Write-Host "Значок: $Out"
