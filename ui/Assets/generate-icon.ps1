Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # Background: rounded square with gradient (deep blue → black-blue)
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $radius = [int]($size * 0.22)

    # Build rounded rect path
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X,                   $rect.Y,                   $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d - 1,      $rect.Y,                   $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d - 1,      $rect.Bottom - $d - 1,     $d, $d, 0,   90)
    $path.AddArc($rect.X,                   $rect.Bottom - $d - 1,     $d, $d, 90,  90)
    $path.CloseFigure()

    # Gradient background
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
        (New-Object System.Drawing.Point 0, 0), `
        (New-Object System.Drawing.Point $size, $size), `
        ([System.Drawing.Color]::FromArgb(255, 60, 80, 180)), `
        ([System.Drawing.Color]::FromArgb(255, 30, 30, 60))
    $g.FillPath($grad, $path)

    # Subtle inner highlight (top)
    $hi = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
        (New-Object System.Drawing.Point 0, 0), `
        (New-Object System.Drawing.Point 0, ([int]($size / 2))), `
        ([System.Drawing.Color]::FromArgb(40, 255, 255, 255)), `
        ([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($hi, $path)

    # Letter "S"
    $fontSize = [single]($size * 0.62)
    try {
        $font = New-Object System.Drawing.Font 'Segoe UI', $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    } catch {
        $font = New-Object System.Drawing.Font 'Arial', $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    }

    $textBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 230, 235, 255))
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center

    $textRect = New-Object System.Drawing.RectangleF 0, ([single](-$size * 0.04)), $size, $size
    $g.DrawString('S', $font, $textBrush, $textRect, $sf)

    $font.Dispose()
    $textBrush.Dispose()
    $grad.Dispose()
    $hi.Dispose()
    $path.Dispose()
    $g.Dispose()

    # Save PNG bytes
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ size = $size; bytes = $ms.ToArray() }
    $ms.Dispose()
    $bmp.Dispose()
}

# Assemble .ico file (PNG-encoded entries, supported by Vista+)
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out

# ICONDIR header
$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type = icon
$bw.Write([uint16]$pngs.Count)    # image count

# Compute offsets
$dataOffset = 6 + ($pngs.Count * 16)
$entries = @()
foreach ($p in $pngs) {
    $entries += @{ size = $p.size; offset = $dataOffset; length = $p.bytes.Length }
    $dataOffset += $p.bytes.Length
}

# Directory entries (ICONDIRENTRY)
foreach ($e in $entries) {
    $sz = if ($e.size -ge 256) { 0 } else { [byte]$e.size }
    $bw.Write([byte]$sz)              # width  (0 = 256)
    $bw.Write([byte]$sz)              # height (0 = 256)
    $bw.Write([byte]0)                # palette
    $bw.Write([byte]0)                # reserved
    $bw.Write([uint16]1)              # planes
    $bw.Write([uint16]32)             # bpp
    $bw.Write([uint32]$e.length)      # size in bytes
    $bw.Write([uint32]$e.offset)      # data offset
}

# Image data
foreach ($p in $pngs) {
    $bw.Write($p.bytes)
}

$bw.Flush()
$icoBytes = $out.ToArray()
$out.Dispose()

$outPath = Join-Path $PSScriptRoot 'app.ico'
[System.IO.File]::WriteAllBytes($outPath, $icoBytes)
Write-Output "Wrote $($icoBytes.Length) bytes -> $outPath"
