$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $out | Out-Null
# Génère l'icône de batterie de l'application avant la compilation.
$icon = Join-Path $out 'BatteryManager.ico'
& $PSHOME\powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'make-icon.ps1') -Output $icon
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $icon)) { throw "La génération de l'icône a échoué." }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw 'Compilateur C# .NET Framework introuvable.' }
$exe = Join-Path $out 'BatteryManager.exe'
$winrt = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll'
$runtime = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll'
$devicesWinmd = Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Devices.winmd'
$foundationWinmd = Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Foundation.winmd'
$storageWinmd = Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Storage.winmd'
& $csc /nologo /target:winexe /optimize+ "/out:$exe" "/win32icon:$icon" `
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll `
  "/reference:$runtime" "/reference:$winrt" "/reference:$devicesWinmd" "/reference:$foundationWinmd" "/reference:$storageWinmd" `
  (Join-Path $root 'BatteryManager.cs')
if ($LASTEXITCODE -ne 0) { throw 'La compilation a echoue.' }
Write-Host "Exécutable créé : $out\BatteryManager.exe" -ForegroundColor Green
