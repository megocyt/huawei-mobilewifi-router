# 从生成的 logo PNG 抠掉白底，缩放打包为多尺寸 HwWidget.ico（16/20/24/32/40/48/64/256）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-Scaled([System.Drawing.Bitmap]$src, [int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $w, $h)))
    $g.Dispose()
    return ,$bmp
}

# BMP 帧：BITMAPINFOHEADER + 自下而上 32bpp BGRA XOR + 全 0 AND 掩码（透明度由 alpha 通道决定）
function Get-IcoBmpBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $strideAbs = [Math]::Abs($data.Stride)
    $buf = New-Object byte[] ($strideAbs * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
    $bmp.UnlockBits($data)
    $andStride = [int](([Math]::Ceiling($w / 32.0)) * 4)
    $xorSize = [int]($strideAbs * $h)
    $andSize = [int]($andStride * $h)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt32]40); $bw.Write([Int32]$w); $bw.Write([Int32]($h * 2))
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
    $bw.Write([UInt32]($xorSize + $andSize))
    $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    for ($y = $h - 1; $y -ge 0; $y--) {                    # 底部行在前
        $off = $y * $strideAbs
        $bw.Write($buf, $off, $strideAbs)
    }
    $andRow = New-Object byte[] $andStride
    for ($y = 0; $y -lt $h; $y++) { $bw.Write($andRow, 0, $andStride) }
    $bw.Flush()
    $arr = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,$arr
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $arr = $ms.ToArray()
    $ms.Dispose()
    return ,$arr
}

$srcPath = $args[0]
$outPath = $args[1]
$full = New-Object System.Drawing.Bitmap($srcPath)
Write-Host "logo: $($full.Width)x$($full.Height)"

$cut = [int]($full.Width * 0.13)                           # 裁掉四周留白 + 右下角 "AI 生成" 水印
$side = $full.Width - 2 * $cut
# 手动裁切到 side x side 的 32bpp ARGB 画布
$cropped = New-Object System.Drawing.Bitmap($side, $side, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($cropped)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.DrawImage($full, (New-Object System.Drawing.Rectangle(0, 0, $side, $side)), (New-Object System.Drawing.Rectangle($cut, $cut, $side, $side)), [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$full.Dispose()

# 接近白色的像素 fade 成全透明 (LockBits 一次扫描)
$rect2 = New-Object System.Drawing.Rectangle(0, 0, $cropped.Width, $cropped.Height)
$data = $cropped.LockBits($rect2, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = [Math]::Abs($data.Stride)
$buf = New-Object byte[] ($stride * $cropped.Height)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
for ($y = 0; $y -lt $cropped.Height; $y++) {
    $row = $y * $stride
    for ($x = 0; $x -lt $cropped.Width; $x++) {
        $i = $row + $x * 4
        $b = [int]$buf[$i]; $gg = [int]$buf[$i+1]; $r = [int]$buf[$i+2]
        # 到白色的欧氏距离：背景接近 0，饱和 glyph 接近 300+
        $dr = 255 - $r; $dg = 255 - $gg; $db = 255 - $b
        $d2 = $dr*$dr + $dg*$dg + $db*$db
        if ($d2 -lt 2025) { $buf[$i+3] = 0 }
        elseif ($d2 -lt 8100) {
            $a = [int](([Math]::Sqrt([double]$d2) - 45) / 45.0 * 255); if ($a -gt $buf[$i+3]) { $a = $buf[$i+3] }
            $buf[$i+3] = [byte]$a
        }
    }
}
[System.Runtime.InteropServices.Marshal]::Copy($buf, 0, $data.Scan0, $buf.Length)
$cropped.UnlockBits($data)

# 扫描 alpha>0 的包围盒，然后按 bbox 裁切 + 小边距（水印已在上面 11% 裁切中丢弃）
$minX = $cropped.Width; $minY = $cropped.Height; $maxX = -1; $maxY = -1
$rect3 = New-Object System.Drawing.Rectangle(0, 0, $cropped.Width, $cropped.Height)
$data3 = $cropped.LockBits($rect3, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride3 = [Math]::Abs($data3.Stride)
$buf3 = New-Object byte[] ($stride3 * $cropped.Height)
[System.Runtime.InteropServices.Marshal]::Copy($data3.Scan0, $buf3, 0, $buf3.Length)
for ($y = 0; $y -lt $cropped.Height; $y++) {
    $row = $y * $stride3
    for ($x = 0; $x -lt $cropped.Width; $x++) {
        $a = $buf3[$row + $x * 4 + 3]
        if ($a -gt 128) {
            if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
$cropped.UnlockBits($data3)
if ($maxX -lt 0) { throw "glyph not detected" }
Write-Host "bbox: ($minX,$minY)-($maxX,$maxY) size=$($maxX-$minX+1)x$($maxY-$minY+1)"
# 直接采用 bbox 作为主体尺寸，加 4% 内边距
$bpad = [int](([Math]::Max($maxX - $minX, $maxY - $minY)) * 0.04)
$bx0 = [Math]::Max(0, $minX - $bpad); $by0 = [Math]::Max(0, $minY - $bpad)
$bx1 = [Math]::Min($cropped.Width - 1, $maxX + $bpad); $by1 = [Math]::Min($cropped.Height - 1, $maxY + $bpad)
$bw = $bx1 - $bx0 + 1; $bh = $by1 - $by0 + 1
$side2 = [Math]::Max($bw, $bh)
$bbox = New-Object System.Drawing.Bitmap($side2, $side2, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gb = [System.Drawing.Graphics]::FromImage($bbox)
$gb.Clear([System.Drawing.Color]::Transparent)
$dx = [int](($side2 - $bw) / 2); $dy = [int](($side2 - $bh) / 2)
$gb.DrawImage($cropped, (New-Object System.Drawing.Rectangle($dx, $dy, $bw, $bh)), (New-Object System.Drawing.Rectangle($bx0, $by0, $bw, $bh)), [System.Drawing.GraphicsUnit]::Pixel)
$gb.Dispose(); $cropped.Dispose()
$cropped = $bbox

# 用 hashtable 列表存帧，避免 PowerShell 嵌套数组被展开
$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$frames = New-Object System.Collections.Generic.List[object]
foreach ($s in $sizes) {
    $b = New-Scaled $cropped $s $s
    if ($s -ge 200) { $bytes = Get-PngBytes $b } else { $bytes = Get-IcoBmpBytes $b }
    [void]$frames.Add(@{ size = $s; bytes = $bytes })
    Write-Host ("frame {0,3}px  {1,7} bytes" -f $s, $bytes.Length)
    $b.Dispose()
}
$cropped.Dispose()

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = [int]$f.size
    $wByte = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write($wByte); $bw.Write($wByte)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$f.bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $f.bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
Write-Host "OK -> $outPath ($((Get-Item $outPath).Length) bytes)"
