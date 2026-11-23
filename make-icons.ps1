# Draws the VSX Deck icons (direction "B · Deck", green): a headband over a key grid, one key lit.
# Original artwork, no third-party marks. Re-run after changing a colour or shape; outputs are committed.
#   assets\vsxdeck.ico                 app + tray icon (16-24 px: simplified "two keys"; larger: full grid)
#   assets\thumbnail.png               1280x640 repo thumbnail
#   com.vsxdeck.sdPlugin\icon*.png     Stream Deck plugin/category icon (28/56)
#   com.vsxdeck.sdPlugin\action*.png   action-list glyph, white (20/40)
#   com.vsxdeck.sdPlugin\key*.png      default key image (72/144)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$assets = Join-Path $root 'assets'; New-Item -ItemType Directory -Force $assets | Out-Null
$sdp = Join-Path $root 'com.vsxdeck.sdPlugin'

$tile = [Drawing.Color]::FromArgb(0x17, 0x18, 0x1c)
$light = [Drawing.Color]::FromArgb(0xec, 0xea, 0xe6)
$dark = [Drawing.Color]::FromArgb(0x3d, 0x40, 0x49)
$accent = [Drawing.Color]::FromArgb(0x35, 0xd0, 0x7f)

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}
function NewCanvas([int]$w, [int]$h) {
    $b = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([Drawing.Color]::Transparent)
    , @($b, $g)
}
function Arc($g, $color, [float]$width, [float]$x, [float]$y, [float]$w, [float]$h) {   # top half of an ellipse
    $pen = New-Object Drawing.Pen $color, $width
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawArc($pen, $x, $y, $w, $h, 180, 180); $pen.Dispose()
}
function Fill($g, $color, $path) { $br = New-Object Drawing.SolidBrush $color; $g.FillPath($br, $path); $br.Dispose(); $path.Dispose() }

# Full mark in a 64-unit square: tile, headband, 3x2 keys with the bottom-middle one lit.
function Mark($g, [float]$s, [float]$ox = 0, [float]$oy = 0, $tileColor = $tile) {
    $g.TranslateTransform($ox, $oy); $g.ScaleTransform($s, $s)
    Fill $g $tileColor (RoundRect 0 0 64 64 15)
    Arc $g $light 4 14 12 36 28
    $keys = @(@(10, 28, $light), @(26, 28, $dark), @(42, 28, $light), @(10, 43, $dark), @(26, 43, $accent), @(42, 43, $dark))
    foreach ($k in $keys) { Fill $g $k[2] (RoundRect $k[0] $k[1] 12 11 3) }
    $g.ResetTransform()
}
# Tray-size mark: headband over two keys, one lit (reads at 16 px).
function SmallMark($g, [float]$s) {
    $g.ScaleTransform($s, $s)
    Fill $g $tile (RoundRect 0 0 64 64 15)
    Arc $g $light 6 12 12 40 32
    Fill $g $light (RoundRect 9 31 20 22 5)
    Fill $g $accent (RoundRect 35 31 20 22 5)
    $g.ResetTransform()
}
# White glyph for the Stream Deck action list (no tile).
function Glyph($g, [float]$size) {
    $s = $size / 52; $g.ScaleTransform($s, $s); $g.TranslateTransform(-6, -9)
    $white = [Drawing.Color]::White
    Arc $g $white 4.5 14 12 36 28
    foreach ($k in @(@(10, 28), @(42, 28), @(26, 43))) { Fill $g $white (RoundRect $k[0] $k[1] 12 11 3) }
    $pen = New-Object Drawing.Pen $white, 1.5
    foreach ($k in @(@(26.75, 28.75), @(10.75, 43.75), @(42.75, 43.75))) { $p = RoundRect $k[0] $k[1] 10.5 9.5 2.5; $g.DrawPath($pen, $p); $p.Dispose() }
    $pen.Dispose(); $g.ResetTransform()
}
function Save($b, $g, $path) { $g.Dispose(); $b.Save($path, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose() }
function Draw([int]$size, [scriptblock]$paint) { $c = NewCanvas $size $size; & $paint $c[1] $size; $c }

# ---- Stream Deck PNGs
foreach ($n in @(@('icon.png', 28), @('icon@2x.png', 56))) { $c = Draw $n[1] { param($g, $z) Mark $g ($z / 64) }; Save $c[0] $c[1] (Join-Path $sdp $n[0]) }
foreach ($n in @(@('action.png', 20), @('action@2x.png', 40))) { $c = Draw $n[1] { param($g, $z) Glyph $g $z }; Save $c[0] $c[1] (Join-Path $sdp $n[0]) }
foreach ($n in @(@('key.png', 72), @('key@2x.png', 144))) { $c = Draw $n[1] { param($g, $z) Mark $g ($z / 64) }; Save $c[0] $c[1] (Join-Path $sdp $n[0]) }

# ---- .ico: small sizes = tray mark (32-bit DIB entries), 256 = full mark (PNG entry)
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$entries = foreach ($z in $sizes) {
    $c = Draw $z { param($g, $sz) if ($sz -le 24) { SmallMark $g ($sz / 64) } else { Mark $g ($sz / 64) } }
    $b = $c[0]; $c[1].Dispose()
    if ($z -eq 256) { $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $data = $ms.ToArray() }
    else {
        $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
        $w.Write([int]40); $w.Write([int]$z); $w.Write([int]($z * 2)); $w.Write([int16]1); $w.Write([int16]32)
        $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
        for ($y = $z - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $z; $x++) { $p = $b.GetPixel($x, $y); $w.Write([byte]$p.B); $w.Write([byte]$p.G); $w.Write([byte]$p.R); $w.Write([byte]$p.A) } }
        $w.Write((New-Object byte[] ([Math]::Ceiling($z / 32) * 4 * $z)))   # AND mask: unused with alpha
        $data = $ms.ToArray()
    }
    $b.Dispose()
    , @($z, $data)
}
$ico = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ico
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $z = $e[0]; $w.Write([byte]($z % 256)); $w.Write([byte]($z % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$e[1].Length); $w.Write([int]$offset); $offset += $e[1].Length
}
foreach ($e in $entries) { $w.Write($e[1]) }
[IO.File]::WriteAllBytes((Join-Path $assets 'vsxdeck.ico'), $ico.ToArray())

# ---- Repo thumbnail 1280x640
$c = NewCanvas 1280 640; $g = $c[1]
$g.Clear($tile)
Mark $g (360 / 64) 120 140 ([Drawing.Color]::FromArgb(0x22, 0x24, 0x2a))
$title = New-Object Drawing.Font 'Segoe UI Semibold', 104, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
$sub = New-Object Drawing.Font 'Segoe UI', 36, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
$fine = New-Object Drawing.Font 'Segoe UI', 20, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
$g.DrawString('VSX Deck', $title, (New-Object Drawing.SolidBrush $light), 560, 200)
$g.DrawString("Switch and Save VSX Presets`nfrom your Stream Deck", $sub, (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0xd9, 0xd6, 0xcf))), 566, 330)
$g.DrawString('Not affiliated with or endorsed by Slate Digital or Steven Slate Audio. VSX is a trademark of its owner.', $fine, (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0x9a, 0x98, 0x8f))), 120, 580)
Save $c[0] $g (Join-Path $assets 'thumbnail.png')

Get-ChildItem $assets, $sdp -Include *.png, *.ico -Recurse | Select-Object Name, Length
