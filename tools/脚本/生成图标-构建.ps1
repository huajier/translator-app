# 生成图标-构建.ps1
# Renders app.ico and tray.ico (16/24/32/48) using System.Drawing and hand-assembles
# the ICO container (ICONDIR + ICONDIRENTRY[4] + DIB/BMP image data).
# The centered CJK glyph is passed as codepoint U+8BD1 (yi4 = "translate").
# NOTE: keep comments in this file ASCII-safe, and NEVER let a tool strip the UTF-8 BOM
# (PowerShell 5.1 reads a BOM-less file as GBK, which corrupts parsing).
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$PrevDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Glyph = [string][char]0x8BD1   # U+8BD1 = the intended character (was 0x8BD5, which is a different one)
$Sizes = @(16, 24, 32, 48)

function New-RoundedPath([System.Drawing.RectangleF]$r, [float]$rad) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [float]($rad * 2.0)
    $p.AddArc([float]$r.X, [float]$r.Y, $d, $d, 180, 90)
    $p.AddArc([float]($r.Right - $d), [float]$r.Y, $d, $d, 270, 90)
    $p.AddArc([float]($r.Right - $d), [float]($r.Bottom - $d), $d, $d, 0, 90)
    $p.AddArc([float]$r.X, [float]($r.Bottom - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Get-Scheme([string]$mode) {
    if ($mode -eq 'app') {
        return @{
            Top    = [System.Drawing.Color]::FromArgb(255, 30, 41, 59)    # #1E293B dark navy
            Bottom = [System.Drawing.Color]::FromArgb(255, 15, 23, 42)    # #0F172A
            Glyph  = [System.Drawing.Color]::FromArgb(255, 56, 189, 248)  # #38BDF8 sky
            Edge   = [System.Drawing.Color]::FromArgb(255, 71, 85, 105)   # #475569
        }
    }
    return @{
        Top    = [System.Drawing.Color]::FromArgb(255, 248, 250, 252)     # #F8FAFC
        Bottom = [System.Drawing.Color]::FromArgb(255, 226, 232, 240)     # #E2E8F0
        Glyph  = [System.Drawing.Color]::FromArgb(255, 29, 78, 216)       # #1D4ED8 blue
        Edge   = [System.Drawing.Color]::FromArgb(255, 71, 85, 105)       # #475569
    }
}

function New-IconBitmap([int]$s, [string]$mode) {
    $c = Get-Scheme $mode
    $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $inset = [Math]::Max(1.0, [double]$s * 0.045)
        $rad = [Math]::Max(2.0, [double]$s * 0.22)
        $rect = [System.Drawing.RectangleF]::new([float]$inset, [float]$inset,
                                                 [float]($s - 2 * $inset), [float]($s - 2 * $inset))
        $path = New-RoundedPath $rect $rad

        $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $c.Top, $c.Bottom, 90.0)
        try {
            $g.FillPath($brush, $path)
        } finally { $brush.Dispose() }

        $edgeW = [Math]::Max(1.0, [double]$s / 28.0)
        $pen = [System.Drawing.Pen]::new($c.Edge, [float]$edgeW)
        try {
            $g.DrawPath($pen, $path)
        } finally { $pen.Dispose() }

        # centered glyph: slightly larger relative size so it stays readable at 16x16
        $fs = if ($s -le 24) { [double]$s * 0.70 } else { [double]$s * 0.60 }
        $font = [System.Drawing.Font]::new('Microsoft YaHei', [float]$fs,
                    [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        try {
            $sf = [System.Drawing.StringFormat]::new()
            $sf.Alignment = [System.Drawing.StringAlignment]::Center
            $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
            $sf.FormatFlags = [System.Drawing.StringFormatFlags]::NoClip
            $gbr = [System.Drawing.SolidBrush]::new($c.Glyph)
            try {
                $g.DrawString($Glyph, $font, $gbr, $rect, $sf)
            } finally { $gbr.Dispose() }
        } finally { $font.Dispose() }
    } finally {
        $g.Dispose()
    }
    return $bmp
}

function New-DibData([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $bd = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                         [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $bd.Stride
        $buf = [byte[]]::new($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $buf, 0, $buf.Length)
    } finally {
        $bmp.UnlockBits($bd)
    }

    # XOR layer (32bpp BGRA), stored bottom-up as required for DIB icons
    $xor = [byte[]]::new($w * $h * 4)
    for ($y = 0; $y -lt $h; $y++) {
        $src = ($h - 1 - $y) * $stride
        $dst = $y * $w * 4
        [Array]::Copy($buf, $src, $xor, $dst, $w * 4)
    }

    # AND mask, rows padded to 32 bits, bit set = transparent (alpha < 128)
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $and = [byte[]]::new($maskStride * $h)
    for ($y = 0; $y -lt $h; $y++) {
        $rowDst = $y * $maskStride
        $srcY = $h - 1 - $y
        $rowSrc = $srcY * $stride
        for ($x = 0; $x -lt $w; $x++) {
            if ($buf[$rowSrc + $x * 4 + 3] -lt 128) {
                $and[$rowDst + ($x -shr 3)] = $and[$rowDst + ($x -shr 3)] -bor (0x80 -shr ($x -band 7))
            }
        }
    }

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    try {
        $bw.Write([uint32]40)              # biSize
        $bw.Write([int32]$w)
        $bw.Write([int32]($h * 2))         # height = XOR + AND
        $bw.Write([uint16]1)               # planes
        $bw.Write([uint16]32)              # bit count
        $bw.Write([uint32]0)               # BI_RGB
        $bw.Write([uint32]($xor.Length + $and.Length))
        $bw.Write([int32]0)                # x ppm
        $bw.Write([int32]0)                # y ppm
        $bw.Write([uint32]0)               # colors used
        $bw.Write([uint32]0)               # important colors
        $bw.Write($xor)
        $bw.Write($and)
        $bw.Flush()
        return , $ms.ToArray()   # unary comma keeps the byte[] from being unrolled by the pipeline
    } finally {
        $bw.Dispose()
        $ms.Dispose()
    }
}

function Write-Ico([string]$path, [object[]]$entries) {
    # $entries: array of @{ S = [int] side ; D = [byte[]] dib payload }
    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    try {
        $bw.Write([uint16]0)                 # ICONDIR.reserved
        $bw.Write([uint16]1)                 # ICONDIR.type = icon
        $bw.Write([uint16]$entries.Count)    # ICONDIR.count
        $offset = 6 + 16 * $entries.Count
        foreach ($e in $entries) {
            $bw.Write([byte]($e.S -band 255))
            $bw.Write([byte]($e.S -band 255))
            $bw.Write([byte]0)               # palette colors
            $bw.Write([byte]0)               # reserved
            $bw.Write([uint16]1)             # color planes
            $bw.Write([uint16]32)            # bits per pixel
            $bw.Write([uint32]$e.D.Length)
            $bw.Write([uint32]$offset)
            $offset += $e.D.Length
        }
        foreach ($e in $entries) { $bw.Write($e.D) }
        $bw.Flush()
        [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    } finally {
        $bw.Dispose()
        $ms.Dispose()
    }
}

function Save-Preview([System.Drawing.Bitmap[]]$maps, [string]$file) {
    $mag = 8
    $pad = 8
    $canvasW = $pad + 16 * $mag + $pad + 16 + $pad + 24 + $pad + 32 + $pad + 48 + $pad
    $canvasH = $pad + 48 * $mag + $pad + 48 + $pad
    $cv = [System.Drawing.Bitmap]::new($canvasW, $canvasH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($cv)
    try {
        $g.Clear([System.Drawing.Color]::FromArgb(255, 120, 128, 138))   # neutral gray backdrop
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::None
        $m16 = $maps[0]
        $g.DrawImage($m16, [int]$pad, [int]$pad, [int](16 * $mag), [int](16 * $mag))
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $x = $pad + 16 * $mag + $pad
        foreach ($sz in @(16, 24, 32, 48)) {
            $m = $maps[[array]::IndexOf(@(16, 24, 32, 48), $sz)]
            $g.DrawImage($m, [int]$x, [int]$pad, [int]$sz, [int]$sz)
            $x += $sz + $pad
        }
        $y = $pad + 48 * $mag + $pad
        $x = $pad
        foreach ($sz in @(16, 24, 32, 48)) {
            $m = $maps[[array]::IndexOf(@(16, 24, 32, 48), $sz)]
            $g.DrawImage($m, [int]$x, [int]$y, [int]$sz, [int]$sz)
            $x += $sz + $pad
        }
        $cv.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $g.Dispose()
        $cv.Dispose()
    }
}

foreach ($mode in @('app', 'tray')) {
    $maps = @()
    $entries = @()
    foreach ($s in $Sizes) {
        $bmp = New-IconBitmap $s $mode
        $maps += , $bmp
        $entries += @{ S = $s; D = (New-DibData $bmp) }
    }
    $outFile = Join-Path $OutDir ($mode + '.ico')
    Write-Ico $outFile $entries
    $len = (Get-Item -LiteralPath $outFile).Length
    Write-Output ("GENERATED {0}  bytes={1}  sizes={2}" -f $outFile, $len, ($Sizes -join ','))

    $prev = Join-Path $PrevDir ("icon_preview_" + $mode + ".png")
    Save-Preview -maps $maps -file $prev
    Write-Output ("PREVIEW  {0}" -f $prev)

    foreach ($m in $maps) { $m.Dispose() }
}
Write-Output "DONE"
