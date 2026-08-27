# app.ico を作り直す: 青→紫グラデーションの角丸 + 中におさまる人型シルエット
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path (Split-Path $MyInvocation.MyCommand.Path) -Parent
$out = Join-Path $root "src\app.ico"

$gradFrom = [System.Drawing.Color]::FromArgb(255, 62, 143, 208)   # AccentBrush #3E8FD0
$gradTo   = [System.Drawing.Color]::FromArgb(255, 138, 91, 216)   # 紫 #8A5BD8
$white    = [System.Drawing.Color]::FromArgb(255, 246, 250, 254)  # OnAccentBrush #F6FAFE

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# 256 キャンバスに描く。シルエットは角丸の内側に完全におさめる
function Render-Master {
    $rect = New-RoundedRect 6 6 244 244 50
    $fgBrush = New-Object System.Drawing.SolidBrush $white
    # 左上 (青) → 右下 (紫) の対角グラデーション
    $gradBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        (New-Object System.Drawing.PointF 6, 6), (New-Object System.Drawing.PointF 250, 250), $gradFrom, $gradTo)

    $bmp = New-Object System.Drawing.Bitmap 256, 256
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.FillPath($gradBrush, $rect)

    # 頭 (中心 128,90 r40)
    $g.FillEllipse($fgBrush, 88, 50, 80, 80)
    # 肩 (スタジアム形: 上辺が丸い横長ピル)。下端は角丸の縁より上で完結する
    $body = New-RoundedRect 52 148 152 74 37
    $g.FillPath($fgBrush, $body)
    $body.Dispose()

    $g.Dispose(); $gradBrush.Dispose(); $fgBrush.Dispose(); $rect.Dispose()
    return $bmp
}

function Resize([System.Drawing.Bitmap]$src, [int]$size) {
    $dst = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    return $dst
}

$master = Render-Master

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) {
    $bmp = if ($s -eq 256) { $master } else { Resize $master $s }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    $ms.Dispose()
    if ($s -ne 256) { $bmp.Dispose() }
}

# ICO に詰める (全エントリ PNG 形式)
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $b = if ($s -eq 256) { 0 } else { $s }
    $w.Write([Byte]$b); $w.Write([Byte]$b)          # 幅・高さ (256 は 0)
    $w.Write([Byte]0); $w.Write([Byte]0)            # パレット数・予約
    $w.Write([UInt16]1); $w.Write([UInt16]32)       # プレーン・ビット深度
    $w.Write([UInt32]$pngs[$s].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$s].Length
}
foreach ($s in $sizes) { $w.Write($pngs[$s]) }
$w.Flush()
[IO.File]::WriteAllBytes($out, $ms.ToArray())
$w.Dispose(); $ms.Dispose()
$master.Dispose()
Write-Host "done: $out ($((Get-Item $out).Length) bytes)"
