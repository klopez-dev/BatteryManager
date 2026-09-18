Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class WinCap {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$p = Start-Process (Join-Path $PSScriptRoot 'dist\BatteryManager.exe') -PassThru
Start-Sleep -Seconds 8
if ($p.HasExited) { Write-Output ("EXITED " + $p.ExitCode); exit }
$proc = Get-Process BatteryManager -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($null -eq $proc) { Write-Output 'pas de fenetre'; exit }
$h = $proc.MainWindowHandle
$r = New-Object WinCap+RECT
[WinCap]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[WinCap]::PrintWindow($h, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save((Join-Path $PSScriptRoot 'ui.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("OK " + $w + "x" + $hh)
