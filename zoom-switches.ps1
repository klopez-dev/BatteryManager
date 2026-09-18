Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'ui.png'))
# Recadre la zone des interrupteurs (droite du footer) et agrandit x3.
$rect = New-Object System.Drawing.Rectangle 700, 610, 340, 110
$crop = New-Object System.Drawing.Bitmap $rect.Width, $rect.Height
$g = [System.Drawing.Graphics]::FromImage($crop)
$g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $rect.Width, $rect.Height), $rect, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$big = New-Object System.Drawing.Bitmap ($rect.Width * 3), ($rect.Height * 3)
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.DrawImage($crop, 0, 0, $rect.Width * 3, $rect.Height * 3)
$g2.Dispose()
$big.Save((Join-Path $PSScriptRoot 'switches-zoom.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
$crop.Dispose()
$src.Dispose()
Write-Output 'ok'
