# kb-write-test.ps1 - checks that writes through InsydeDCHU actually reach the hardware,
# using the keyboard backlight (values copied from the vendor Control Center):
#   brightness : cmd 0x67, top byte 0xF4, value 0..255   (vendor uses 191)
#   color      : cmd 0x67, top byte 0xF0, value = B<<16 | R<<8 | G
# Watch the keyboard while it runs. Afterwards, re-pick your colour in the vendor app.
# Usage: powershell -ExecutionPolicy Bypass -File .\kb-write-test.ps1

param([string]$DllPath)
$ErrorActionPreference = 'Stop'
$work = Join-Path $env:LOCALAPPDATA 'CHelperProbe'
New-Item -ItemType Directory -Force $work | Out-Null
if (-not $DllPath) {
    $pkg = Get-AppxPackage -Name '*FnhotkeysandOSD*' | Select-Object -First 1
    if (-not $pkg) { throw "Vendor package not found. Pass -DllPath to InsydeDCHU.dll" }
    $DllPath = Join-Path $pkg.InstallLocation 'FnKey\InsydeDCHU.dll'
}
$local = Join-Path $work 'InsydeDCHU.dll'
if (-not (Test-Path $local)) { Copy-Item $DllPath $local -Force }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class DchuKb {
    const string D = @"$local";
    [DllImport(D)] public static extern int SetDCHU_Data(int cmd, byte[] data, int len);
}
"@
function Kb([int]$sub, [int]$value, [string]$what) {
    $b = [BitConverter]::GetBytes([int]$value); $b[3] = [byte]$sub
    $r = [DchuKb]::SetDCHU_Data(0x67, $b, 4)
    Write-Host ("{0,-28} (return {1})" -f $what, $r)
    Start-Sleep -Milliseconds 1500
}

Write-Host 'Watch your keyboard...' -ForegroundColor Cyan
Kb 0xF4 255 'Brightness: max'
Kb 0xF0 (0 -shl 16 -bor 255 -shl 8 -bor 0) 'Color: red'
Kb 0xF0 (0 -shl 16 -bor 0 -shl 8 -bor 255) 'Color: green'
Kb 0xF0 (255 -shl 16 -bor 0 -shl 8 -bor 0) 'Color: blue'
Kb 0xF4 0 'Brightness: off'
Kb 0xF4 191 'Brightness: back on (191)'
Write-Host 'Did the keyboard change? Re-pick your colour in the vendor app afterwards.' -ForegroundColor Yellow
