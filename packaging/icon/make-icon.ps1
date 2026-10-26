<#
.SYNOPSIS
    Draws the xpp-lens icon: a lens over an X on a violet tile (X++ code under the lens).
    Writes xpplens.ico (16-256 px, PNG frames) and icon.png (512 px) next to this script.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float] $x, [float] $y, [float] $w, [float] $h, [float] $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float] $size

    # Tile: violet, lighter at the top.
    $margin = [Math]::Max(0.5, $s * 0.03)
    $tile = New-RoundedRect $margin $margin ($s - 2 * $margin) ($s - 2 * $margin) ($s * 0.2)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s),
        ([System.Drawing.Color]::FromArgb(255, 134, 76, 204)), ([System.Drawing.Color]::FromArgb(255, 78, 36, 138))
    $g.FillPath($grad, $tile)

    $white = [System.Drawing.Color]::White
    # Lens: a ring, centred a little up and left, with a handle to the bottom right.
    $cx = $s * 0.43; $cy = $s * 0.43; $r = $s * 0.24
    $ring = [Math]::Max(1.5, $s * 0.075)
    if ($size -ge 24) {
        # An X inside the lens (left out at 16-20 px, where it would blur into the ring).
        $x = New-Object System.Drawing.Pen $white, ([Math]::Max(1.5, $s * 0.06))
        $x.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $x.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $a = $r * 0.4
        $g.DrawLine($x, $cx - $a, $cy - $a, $cx + $a, $cy + $a)
        $g.DrawLine($x, $cx - $a, $cy + $a, $cx + $a, $cy - $a)
    }
    if ($size -ge 32) {
        # "++" below the lens, left of the handle: together X++.
        $plus = New-Object System.Drawing.Pen $white, ([Math]::Max(1.5, $s * 0.05))
        $plus.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $plus.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $p = $s * 0.055; $py = $s * 0.83
        foreach ($px in ($s * 0.24), ($s * 0.42)) {
            $g.DrawLine($plus, $px - $p, $py, $px + $p, $py)
            $g.DrawLine($plus, $px, $py - $p, $px, $py + $p)
        }
    }
    $pen = New-Object System.Drawing.Pen $white, $ring
    $g.DrawEllipse($pen, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
    $handle = New-Object System.Drawing.Pen $white, ([Math]::Max(2, $s * 0.11))
    $handle.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handle.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $a = 0.7071 * ($r + $ring / 2)
    $g.DrawLine($handle, $cx + $a, $cy + $a, $s * 0.76, $s * 0.76)
    $g.Dispose()
    return $bmp
}

$dir = $PSScriptRoot
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($n in $sizes) {
    $bmp = New-IconBitmap $n
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO: header, one directory entry per size, then the PNG frames.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]
    $dim = if ($n -ge 256) { 0 } else { $n }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$frames[$i].Length); $w.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $dir 'xpplens.ico'), $out.ToArray())

$big = New-IconBitmap 512
$big.Save((Join-Path $dir 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
Write-Host "written xpplens.ico and icon.png in $dir"
