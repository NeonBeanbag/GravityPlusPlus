# 由 Assets/icon-src.png 生成多尺寸 icon.ico（PNG 条目）——只做确定性缩放
Add-Type -AssemblyName System.Drawing
$src = Join-Path $PSScriptRoot 'Assets\icon-src.png'
$out = Join-Path $PSScriptRoot 'Assets\icon.ico'
$tmp = Join-Path $env:TEMP ('gicon_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$img = [System.Drawing.Image]::FromFile($src)
Write-Output ("源图 " + $img.Width + "x" + $img.Height)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $g.DrawImage($img, $rect)
    $g.Dispose()
    $f = Join-Path $tmp "$s.png"
    $bmp.Save($f, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $entries += [pscustomobject]@{ Size = $s; Bytes = [System.IO.File]::ReadAllBytes($f) }
}
$img.Dispose()
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ms)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$entries.Count)
$off = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    if ($e.Size -ge 256) { $b = [byte]0 } else { $b = [byte]$e.Size }
    $w.Write($b); $w.Write($b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$e.Bytes.Length); $w.Write([uint32]$off)
    $off += $e.Bytes.Length
}
foreach ($e in $entries) { $w.Write($e.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
$w.Dispose(); $ms.Dispose()
Remove-Item -Recurse -Force $tmp
Write-Output ("icon.ico = " + (Get-Item $out).Length + " 字节")
foreach ($e in $entries) { Write-Output ("  " + $e.Size + "px -> " + $e.Bytes.Length + " B") }
