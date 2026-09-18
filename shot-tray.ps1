Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$w = 400; $h = 60
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Width - $w, $bounds.Height - $h, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
$big = New-Object System.Drawing.Bitmap ($w * 3), ($h * 3)
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.DrawImage($bmp, 0, 0, $w * 3, $h * 3)
$g2.Dispose()
$big.Save((Join-Path $PSScriptRoot 'tray-live.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
$bmp.Dispose()
Write-Output 'ok'
