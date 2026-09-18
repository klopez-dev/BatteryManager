$path = Join-Path $PSScriptRoot 'dist\BatteryManager.ico'
$b = [System.IO.File]::ReadAllBytes($path)
Write-Output ("Taille fichier: " + $b.Length)
Write-Output ("Reserve=" + [BitConverter]::ToUInt16($b, 0) + " Type=" + [BitConverter]::ToUInt16($b, 2) + " Count=" + [BitConverter]::ToUInt16($b, 4))
$count = [BitConverter]::ToUInt16($b, 4)
for ($i = 0; $i -lt $count; $i++) {
    $o = 6 + $i * 16
    $w = $b[$o]; $h = $b[$o + 1]
    $len = [BitConverter]::ToUInt32($b, $o + 8)
    $off = [BitConverter]::ToUInt32($b, $o + 12)
    $sig = ($b[$off..($off + 3)] | ForEach-Object { $_.ToString('X2') }) -join ' '
    Write-Output ("entree $i : " + $w + "x" + $h + " len=" + $len + " off=" + $off + " sig=" + $sig + " fin=" + ($off + $len))
}
Write-Output ("Offset attendu 1re entree: " + (6 + 16 * $count))
