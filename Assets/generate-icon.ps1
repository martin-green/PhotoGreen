# Generates Assets/PhotoGreen.ico: a green camera app icon.
# Re-run this script if the logo needs tweaking.
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = @()

function New-IconBitmap([int]$S) {
	$bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$g = [System.Drawing.Graphics]::FromImage($bmp)
	$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
	$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
	$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
	$g.Clear([System.Drawing.Color]::Transparent)

	# Work in a 256-unit design space, then scale to the requested size.
	$u = $S / 256.0
	function P([double]$v) { return [float]($v * $u) }

	# Palette: deep green body with a brighter green face for contrast at small sizes.
	$bodyDark = [System.Drawing.Color]::FromArgb(255, 27, 94, 32)
	$bodyMid = [System.Drawing.Color]::FromArgb(255, 56, 142, 60)
	$bodyLite = [System.Drawing.Color]::FromArgb(255, 76, 175, 80)
	$accent = [System.Drawing.Color]::FromArgb(255, 129, 199, 132)

	# --- Camera body (rounded rectangle) ---
	$bx = P 18; $by = P 74; $bw = P 220; $bh = P 148; $r = P 30
	$body = New-Object System.Drawing.Drawing2D.GraphicsPath
	$body.AddArc($bx, $by, $r, $r, 180, 90)
	$body.AddArc($bx + $bw - $r, $by, $r, $r, 270, 90)
	$body.AddArc($bx + $bw - $r, $by + $bh - $r, $r, $r, 0, 90)
	$body.AddArc($bx, $by + $bh - $r, $r, $r, 90, 90)
	$body.CloseFigure()

	# --- Viewfinder hump on top ---
	$hx = P 74; $hy = P 48; $hw = P 84; $hh = P 34; $hr = P 14
	$hump = New-Object System.Drawing.Drawing2D.GraphicsPath
	$hump.AddArc($hx, $hy, $hr, $hr, 180, 90)
	$hump.AddArc($hx + $hw - $hr, $hy, $hr, $hr, 270, 90)
	$hump.AddLine([float]($hx + $hw), [float]($hy + $hh), [float]$hx, [float]($hy + $hh))
	$hump.CloseFigure()

	$humpBrush = New-Object System.Drawing.SolidBrush($bodyDark)
	$g.FillPath($humpBrush, $hump)

	$bodyRect = New-Object System.Drawing.RectangleF($bx, $by, $bw, $bh)
	$bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
		$bodyRect, $bodyLite, $bodyDark, 90.0)
	$g.FillPath($bodyBrush, $body)

	# --- Lens ---
	$cx = P 128; $cy = P 148
	$outerR = P 56
	$g.FillEllipse((New-Object System.Drawing.SolidBrush($bodyDark)),
		[float]($cx - $outerR), [float]($cy - $outerR), [float]($outerR * 2), [float]($outerR * 2))

	$ringR = P 46
	$g.FillEllipse((New-Object System.Drawing.SolidBrush($accent)),
		[float]($cx - $ringR), [float]($cy - $ringR), [float]($ringR * 2), [float]($ringR * 2))

	$glassR = P 36
	$glassRect = New-Object System.Drawing.RectangleF(
		[float]($cx - $glassR), [float]($cy - $glassR), [float]($glassR * 2), [float]($glassR * 2))
	$glassBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
		$glassRect, $bodyMid, $bodyDark, 45.0)
	$g.FillEllipse($glassBrush, $glassRect)

	# Highlight glint keeps the lens readable even at 16px.
	$hlR = $glassR * 0.42
	$hlBrush = New-Object System.Drawing.SolidBrush(
		[System.Drawing.Color]::FromArgb(150, 220, 255, 220))
	$g.FillEllipse($hlBrush,
		[float]($cx - $glassR * 0.55), [float]($cy - $glassR * 0.70),
		[float]($hlR * 2), [float]($hlR * 2))

	# --- Shutter button ---
	$g.FillEllipse((New-Object System.Drawing.SolidBrush($accent)),
		(P 196), (P 96), (P 22), (P 22))

	$g.Dispose()
	return $bmp
}

foreach ($s in $sizes) {
	$bmp = New-IconBitmap $s
	$ms = New-Object System.IO.MemoryStream
	$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
	$pngs += , $ms.ToArray()
	$bmp.Dispose()
	$ms.Dispose()
}

# --- Pack into a PNG-compressed .ico (supported on Windows Vista and later) ---
$out = Join-Path $PSScriptRoot 'PhotoGreen.ico'
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([uint16]0)               # reserved
$bw.Write([uint16]1)               # type: icon
$bw.Write([uint16]$sizes.Count)    # image count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
	$s = $sizes[$i]
	# 256 is encoded as 0 in the directory entry.
	$bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
	$bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
	$bw.Write([byte]0)             # palette count
	$bw.Write([byte]0)             # reserved
	$bw.Write([uint16]1)           # colour planes
	$bw.Write([uint16]32)          # bits per pixel
	$bw.Write([uint32]$pngs[$i].Length)
	$bw.Write([uint32]$offset)
	$offset += $pngs[$i].Length
}

foreach ($png in $pngs) { $bw.Write($png) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Host "Wrote $out ($((Get-Item $out).Length) bytes, $($sizes.Count) sizes)"
