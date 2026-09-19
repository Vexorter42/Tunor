# Generates Assets/app.ico — the Nyx mark: a green crescent moon on near-black.
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()

# Palette (matches Styles/Theme.xaml)
$bgTop    = [System.Drawing.Color]::FromArgb(255, 42, 48, 58)
$bgBottom = [System.Drawing.Color]::FromArgb(255, 20, 23, 28)
$accent   = [System.Drawing.Color]::FromArgb(255, 61, 220, 92)

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # --- rounded-square background ---
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $radius = [int]($size * 0.24)
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X,              $rect.Y,               $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d - 1, $rect.Y,               $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d - 1, $rect.Bottom - $d - 1, $d, $d,   0, 90)
    $path.AddArc($rect.X,              $rect.Bottom - $d - 1, $d, $d,  90, 90)
    $path.CloseFigure()

    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
        (New-Object System.Drawing.Point 0, 0), `
        (New-Object System.Drawing.Point 0, $size), $bgTop, $bgBottom
    $g.FillPath($grad, $path)

    # --- crescent: accent disc minus an offset disc punched back to background ---
    $old = $g.Clip
    $g.SetClip($path)

    $cx = $size * 0.50
    $cy = $size * 0.50
    $r  = $size * 0.30

    $moon = New-Object System.Drawing.SolidBrush $accent
    $g.FillEllipse($moon, [single]($cx - $r), [single]($cy - $r), [single]($r * 2), [single]($r * 2))

    # punch-out disc, offset up-right, filled with the local background tone
    $punchR = $r * 0.86
    $px = $cx + $r * 0.42
    $py = $cy - $r * 0.30
    $punchGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
        (New-Object System.Drawing.Point 0, 0), `
        (New-Object System.Drawing.Point 0, $size), $bgTop, $bgBottom
    $g.FillEllipse($punchGrad, [single]($px - $punchR), [single]($py - $punchR),
                   [single]($punchR * 2), [single]($punchR * 2))

    # --- small star (skip on tiny sizes where it turns to mush) ---
    if ($size -ge 32) {
        $sx = $size * 0.70
        $sy = $size * 0.26
        $sr = $size * 0.055
        $star = New-Object System.Drawing.Drawing2D.GraphicsPath
        [System.Drawing.PointF[]]$pts = @(
            [System.Drawing.PointF]::new([single]$sx, [single]($sy - $sr)),
            [System.Drawing.PointF]::new([single]($sx + $sr * 0.32), [single]($sy - $sr * 0.32)),
            [System.Drawing.PointF]::new([single]($sx + $sr), [single]$sy),
            [System.Drawing.PointF]::new([single]($sx + $sr * 0.32), [single]($sy + $sr * 0.32)),
            [System.Drawing.PointF]::new([single]$sx, [single]($sy + $sr)),
            [System.Drawing.PointF]::new([single]($sx - $sr * 0.32), [single]($sy + $sr * 0.32)),
            [System.Drawing.PointF]::new([single]($sx - $sr), [single]$sy),
            [System.Drawing.PointF]::new([single]($sx - $sr * 0.32), [single]($sy - $sr * 0.32))
        )
        $star.AddPolygon($pts)
        $g.FillPath($moon, $star)
        $star.Dispose()
    }

    $g.Clip = $old
    $moon.Dispose(); $punchGrad.Dispose(); $grad.Dispose(); $path.Dispose(); $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ size = $size; bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

# --- assemble .ico (PNG-encoded entries) ---
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)

$dataOffset = 6 + ($pngs.Count * 16)
$entries = @()
foreach ($p in $pngs) {
    $entries += @{ size = $p.size; offset = $dataOffset; length = $p.bytes.Length }
    $dataOffset += $p.bytes.Length
}
foreach ($e in $entries) {
    $sz = if ($e.size -ge 256) { 0 } else { [byte]$e.size }
    $bw.Write([byte]$sz); $bw.Write([byte]$sz); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$e.length); $bw.Write([uint32]$e.offset)
}
foreach ($p in $pngs) { $bw.Write($p.bytes) }
$bw.Flush()
$bytes = $out.ToArray(); $out.Dispose()

$outPath = Join-Path $PSScriptRoot 'app.ico'
[System.IO.File]::WriteAllBytes($outPath, $bytes)
Write-Output "Wrote $($bytes.Length) bytes -> $outPath"
