# Regenerates tezgah.ico (16-256 px, PNG-compressed entries): a rounded accent square with a minimal
# workbench glyph (bench top + two legs + a small anvil block). Run from any folder:
#   pwsh -File src/Tezgah.App/Assets/make-icon.ps1
Add-Type -AssemblyName System.Drawing

# largest first: WPF's Image control shows the first frame of an .ico
$sizes = 256, 128, 64, 48, 40, 32, 24, 20, 16
$out = Join-Path $PSScriptRoot 'tezgah.ico'
$pngs = @()

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(0.5, $s * 0.04)
    $bg = New-RoundedRect $pad $pad ($s - 2 * $pad) ($s - 2 * $pad) ($s * 0.22)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 0, 120, 212)), ([System.Drawing.Color]::FromArgb(255, 0, 76, 153))
    $g.FillPath($grad, $bg)

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $u = $s / 16.0
    # anvil block on the bench
    $g.FillPath($white, (New-RoundedRect (5.0 * $u) (3.6 * $u) (6.0 * $u) (2.6 * $u) ([Math]::Max(0.5, 0.6 * $u))))
    # bench top
    $g.FillPath($white, (New-RoundedRect (2.6 * $u) (7.0 * $u) (10.8 * $u) (2.2 * $u) ([Math]::Max(0.5, 0.6 * $u))))
    # legs
    $g.FillRectangle($white, [float](4.0 * $u), [float](9.0 * $u), [float](1.8 * $u), [float](4.4 * $u))
    $g.FillRectangle($white, [float](10.2 * $u), [float](9.0 * $u), [float](1.8 * $u), [float](4.4 * $u))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs += , $ms.ToArray()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()
Write-Host "wrote $out"
