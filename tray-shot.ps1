Add-Type -AssemblyName System.Drawing
# Capture la zone de notification (coin bas-droit de l'ecran principal).
Add-Type -AssemblyName System.Windows.Forms
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$w = 400; $h = 80
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Width - $w, $bounds.Height - $h, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
$big = New-Object System.Drawing.Bitmap ($w * 4), ($h * 4)
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.DrawImage($bmp, 0, 0, $w * 4, $h * 4)
$g2.Dispose()
$big.Save((Join-Path $PSScriptRoot 'tray-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
$bmp.Dispose()
Write-Output 'ok'
