# Vẽ icon app (ô vuông bo góc màu indigo + biểu tượng merge 2 nhánh) bằng GDI+,
# xuất GitLabMR/assets/icon.png (1024px) và icon.ico (16..256px, PNG-in-ICO).
#   powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root "GitLabMR/assets"
New-Item -ItemType Directory -Force $outDir | Out-Null

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

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.PixelOffsetMode = "HighQuality"; $g.CompositingQuality = "HighQuality"
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 1024.0

    # Nền: bo góc kiểu macOS, gradient indigo
    $pad = 64 * $s
    $bg = New-RoundedRect $pad $pad ($size - 2 * $pad) ($size - 2 * $pad) (200 * $s)
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 110, 120, 245)), ([System.Drawing.Color]::FromArgb(255, 62, 72, 196)), 55
    $g.FillPath($brush, $bg)
    # Viền sáng nhẹ phía trên cho có chiều sâu
    $hl = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), (6 * $s)
    $g.DrawPath($hl, $bg)

    # Biểu tượng merge: nhánh chính dọc bên trái, nhánh phụ cong nhập vào
    $white = [System.Drawing.Color]::White
    $pen = New-Object System.Drawing.Pen $white, (64 * $s)
    $pen.StartCap = "Round"; $pen.EndCap = "Round"; $pen.LineJoin = "Round"
    $ax = 360 * $s; $topY = 300 * $s; $botY = 724 * $s       # nhánh chính
    $bx = 664 * $s; $by = 420 * $s                            # đầu nhánh phụ
    $g.DrawLine($pen, $ax, $topY, $ax, $botY)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddBezier($bx, $by, $bx, 560 * $s, $ax, 520 * $s, $ax, 640 * $s)
    $g.DrawPath($pen, $path)

    # 3 nút commit
    $fill = New-Object System.Drawing.SolidBrush $white
    $ring = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 70, 80, 205))
    foreach ($pt in @(@($ax, $topY), @($ax, $botY), @($bx, $by))) {
        $R = 92 * $s; $r2 = 46 * $s
        $g.FillEllipse($fill, $pt[0] - $R, $pt[1] - $R, 2 * $R, 2 * $R)
        $g.FillEllipse($ring, $pt[0] - $r2, $pt[1] - $r2, 2 * $r2, 2 * $r2)
    }
    $g.Dispose()
    return $bmp
}

# PNG lớn (dùng cho macOS .icns và logo)
$big = Draw-Icon 1024
$big.Save((Join-Path $outDir "icon.png"), [System.Drawing.Imaging.ImageFormat]::Png)

# ICO nhiều kích thước (mỗi entry là 1 PNG)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $b = Draw-Icon $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    , $ms.ToArray()
}
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $pngs[$i]
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $w.Write($data) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir "icon.ico"), $ico.ToArray())
Write-Host "OK -> $outDir (icon.png, icon.ico)"
