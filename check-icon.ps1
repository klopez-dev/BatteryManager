Add-Type -Namespace W2 -Name Enum2 -MemberDefinition @'
[DllImport("kernel32.dll",SetLastError=true)] public static extern bool EnumResourceNames(IntPtr h, IntPtr t, EnumResNameProc cb, IntPtr p);
public delegate bool EnumResNameProc(IntPtr h, IntPtr t, IntPtr n, IntPtr p);
'@

Add-Type -Namespace W -Name Ico -MemberDefinition @'
[DllImport("kernel32.dll",SetLastError=true)] public static extern IntPtr LoadLibraryEx(string f, IntPtr h, uint fl);
[DllImport("kernel32.dll",SetLastError=true)] public static extern IntPtr FindResource(IntPtr h, IntPtr n, IntPtr t);
[DllImport("kernel32.dll",SetLastError=true)] public static extern IntPtr LoadResource(IntPtr h, IntPtr r);
[DllImport("kernel32.dll",SetLastError=true)] public static extern IntPtr LockResource(IntPtr r);
[DllImport("kernel32.dll",SetLastError=true)] public static extern uint SizeofResource(IntPtr h, IntPtr r);
'@

$exe = (Resolve-Path (Join-Path $PSScriptRoot 'dist\BatteryManager.exe')).Path
$h = [W.Ico]::LoadLibraryEx($exe, [IntPtr]::Zero, 0x2)
$found = New-Object System.Collections.ArrayList
$cb = [W2.Enum2+EnumResNameProc] { param($hh, $tt, $nn, $pp) [void]$found.Add([int]$nn); return $true }
$ok = [W2.Enum2]::EnumResourceNames($h, [IntPtr]3, $cb, [IntPtr]::Zero)
Write-Output ("RT_ICON ids: " + ($found -join ','))
$dir = [W.Ico]::FindResource($h, [IntPtr]$found[0], [IntPtr]3)
$res = [W.Ico]::LoadResource($h, $dir)
$p = [W.Ico]::LockResource($res)
$size = [W.Ico]::SizeofResource($h, $dir)
$bytes = New-Object byte[] $size
[System.Runtime.InteropServices.Marshal]::Copy($p, $bytes, 0, $size)
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'embedded-res.ico'), $bytes)
Write-Output ("Taille=" + $size)
Write-Output (($bytes[0..15] | ForEach-Object { $_.ToString('X2') }) -join '-')

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 240, 120
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
$ico = New-Object System.Drawing.Icon((Join-Path $PSScriptRoot 'embedded-res.ico'))
$g.DrawIcon($ico, (New-Object System.Drawing.Rectangle 10, 10, 100, 100))
$g.Dispose()
$bmp.Save((Join-Path $PSScriptRoot 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output 'preview ok'
