# Draws the program's icon (a gold map pin on a dark tile) and writes it as tools\Exporter\app.ico.
# Only needed when the icon is to be changed: the .ico file itself is part of the source.
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param([string]$Out)
$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'Exporter\app.ico' }
Add-Type -AssemblyName System.Drawing

function New-Picture([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0

    # the tile
    $r = 13 * $s
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $size - 2 * $s
    $o = 1 * $s
    $tile.AddArc($o, $o, 2 * $r, 2 * $r, 180, 90)
    $tile.AddArc($o + $w - 2 * $r, $o, 2 * $r, 2 * $r, 270, 90)
    $tile.AddArc($o + $w - 2 * $r, $o + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $tile.AddArc($o, $o + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $tile.CloseFigure()
    $back = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $size),
        ([System.Drawing.Color]::FromArgb(255, 38, 46, 62)), ([System.Drawing.Color]::FromArgb(255, 18, 21, 27))
    $g.FillPath($back, $tile)

    # the pin: a circle that runs down into a point
    $cx = 32 * $s; $cy = 26 * $s; $rad = 15 * $s
    $pin = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pin.AddArc($cx - $rad, $cy - $rad, 2 * $rad, 2 * $rad, 150, 240)
    $pin.AddLine($cx + $rad * 0.866, $cy + $rad * 0.5, $cx, 55 * $s)
    $pin.CloseFigure()
    $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 242, 193, 78))
    $g.FillPath($gold, $pin)
    $hole = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 24, 28, 36))
    $hr = 6 * $s
    $g.FillEllipse($hole, $cx - $hr, $cy - $hr, 2 * $hr, 2 * $hr)
    $g.Dispose()
    $bmp
}

# One entry per size: plain 32-bit pictures for the small ones (every Windows part reads those), PNG for 256.
$entries = foreach ($size in 16, 20, 24, 32, 48, 64, 256) {
    $bmp = New-Picture $size
    $ms = New-Object System.IO.MemoryStream
    if ($size -eq 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([int]40); $bw.Write([int]$size); $bw.Write([int]($size * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $row = [int]([Math]::Ceiling($size / 32.0) * 4)       # the unused 1-bit mask: rows padded to 4 bytes
        $bw.Write((New-Object byte[] ($row * $size)))
        $bw.Flush()
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
}

$file = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $file
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$e.Bytes.Length); $w.Write([int]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $w.Write($e.Bytes) }
$w.Flush()
[IO.File]::WriteAllBytes($Out, $file.ToArray())
Write-Host "wrote $Out ($($file.Length) bytes, sizes: $(($entries | ForEach-Object Size) -join ', '))"
