# ============================================================================
#  FCT-Distributor app icon generator
#
#  Usage:  powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1 app_icon.ico
#
#  Design: deep-blue rounded tile (gradient) + white folder + yellow down arrow
#          => "data dropped into an existing folder"
#  Frames: 16/20/24/32/40/48/64 as classic DIB (System.Drawing.Icon safe)
#          128/256 as PNG (shell / high-DPI)
#  NOTE:   keep this file ASCII-only - Windows PowerShell 5.1 mis-decodes
#          UTF-8-no-BOM scripts and can break parsing.
# ============================================================================
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([double]$x,[double]$y,[double]$w,[double]$h,[double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    function U([double]$v) { return $v * $s }

    # ---- background: rounded tile with diagonal gradient
    $bg = New-RoundedPath (U 6) (U 6) (U 244) (U 244) (U 52)
    $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 26, 58, 122),
        [System.Drawing.Color]::FromArgb(255, 56, 140, 220),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $g.FillPath($br, $bg)

    # ---- folder back plate + tab
    $folderBack = [System.Drawing.Color]::FromArgb(255, 219, 234, 250)
    $bbr = New-Object System.Drawing.SolidBrush($folderBack)
    $tab = New-RoundedPath (U 40) (U 118) (U 92) (U 46) (U 10)
    $g.FillPath($bbr, $tab)
    $body = New-RoundedPath (U 40) (U 138) (U 176) (U 82) (U 14)
    $g.FillPath($bbr, $body)

    # ---- yellow down arrow (data dropped into the folder)
    $accent = [System.Drawing.Color]::FromArgb(255, 255, 198, 61)
    $abr = New-Object System.Drawing.SolidBrush($accent)
    $shaft = New-RoundedPath (U 112) (U 34) (U 32) (U 86) (U 8)
    $g.FillPath($abr, $shaft)
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $head.AddPolygon(@(
        (New-Object System.Drawing.PointF((U 90), (U 104))),
        (New-Object System.Drawing.PointF((U 166), (U 104))),
        (New-Object System.Drawing.PointF((U 128), (U 164)))
    ))
    $g.FillPath($abr, $head)

    # ---- folder front plate (covers arrow tip => lands inside)
    $fbr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $front = New-RoundedPath (U 40) (U 152) (U 176) (U 68) (U 14)
    $g.FillPath($fbr, $front)

    # ---- thin divider line on the front plate
    if ($size -ge 32) {
        $pen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(255, 150, 180, 214), [float](U 6))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($pen, [float](U 66), [float](U 190), [float](U 190), [float](U 190))
        $pen.Dispose()
    }

    $g.Dispose()
    return $bmp
}

function New-DibBytes([System.Drawing.Bitmap]$bmp) {
    # classic BMP/DIB frame (BITMAPINFOHEADER + 32bpp BGRA bottom-up + 1bpp AND mask)
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt32]40)              # biSize
    $bw.Write([Int32]$w)               # biWidth
    $bw.Write([Int32]($h * 2))         # biHeight (XOR + AND)
    $bw.Write([UInt16]1)               # biPlanes
    $bw.Write([UInt16]32)              # biBitCount
    $bw.Write([UInt32]0)               # BI_RGB
    $bw.Write([UInt32]($w * $h * 4))   # biSizeImage
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([Byte]$c.B); $bw.Write([Byte]$c.G)
            $bw.Write([Byte]$c.R); $bw.Write([Byte]$c.A)
        }
    }
    $stride = [Math]::Floor(($w + 31) / 32) * 4
    for ($y = $h - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $stride
        for ($x = 0; $x -lt $w; $x++) {
            if ($bmp.GetPixel($x, $y).A -lt 128) {
                $row[[Math]::Floor($x / 8)] = $row[[Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8))
            }
        }
        $bw.Write($row)
    }
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,([byte[]]$bytes)
}

# <= 64px as DIB (max compatibility with System.Drawing.Icon), 128/256 as PNG
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    if ($sz -le 64) {
        $bytes = New-DibBytes $bmp
    } else {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = [byte[]]$ms.ToArray(); $ms.Dispose()
    }
    $frames += , @{ Size = $sz; Bytes = $bytes }
    $bmp.Dispose()
}

$out = $args[0]
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]([byte[]]$f.Bytes).Length)
    $bw.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f.Bytes, 0, ([byte[]]$f.Bytes).Length) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()
Write-Output ("wrote {0} ({1} frames, {2} bytes)" -f $out, $frames.Count, (Get-Item $out).Length)
