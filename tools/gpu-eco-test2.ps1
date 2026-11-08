# gpu-eco-test2.ps1 - tests the vendor's "Turn Off dGPU" flag (bit 6 = +64) on the
# power-profile command (0x79, sub 0x19) while staying in Entertainment (profile 3).
#
#   step 1: profile = 3 + 64  -> Entertainment with "dGPU off" flag   (expect NVIDIA -> Unknown)
#   step 2: profile = 3       -> plain Entertainment, as the vendor sends it (expect NVIDIA -> OK)
# The script always sends plain Entertainment (3) before exiting.
#
# Before running: select Entertainment in the vendor app, close games / anything using the NVIDIA GPU.
# Usage: powershell -ExecutionPolicy Bypass -File .\gpu-eco-test2.ps1

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
public static class DchuP {
    const string D = @"$local";
    [DllImport(D)] public static extern int SetDCHU_Data(int cmd, byte[] data, int len);
}
"@

# Same packing as the vendor: value in the low byte, sub-command in the top byte.
function Set-Profile([int]$value) {
    $b = [BitConverter]::GetBytes([int]$value)
    $b[3] = 0x19
    $r = [DchuP]::SetDCHU_Data(0x79, $b, $b.Length)
    Write-Host ("  sent 0x79/0x19 = {0} (return {1})" -f $value, $r) -ForegroundColor DarkGray
}
function Show-Gpus([string]$title) {
    Write-Host "`n== $title ==" -ForegroundColor Cyan
    Get-PnpDevice -Class Display | Format-Table -AutoSize Status, FriendlyName | Out-String | Write-Host
}

Show-Gpus 'Before (should be Entertainment)'
Read-Host 'Press Enter to send Entertainment + dGPU off (3 + 64)'
try {
    Set-Profile (3 + 64)
    Write-Host 'Waiting 10 seconds...'; Start-Sleep 10
    Show-Gpus 'After Entertainment + dGPU off'
    Read-Host 'Note the NVIDIA status, then press Enter to restore plain Entertainment'
}
finally {
    Set-Profile 3
    Write-Host 'Waiting 10 seconds...'; Start-Sleep 10
    Show-Gpus 'After plain Entertainment (restored)'
}
Write-Host 'Done. If NVIDIA stays Unknown, pick Entertainment again in the vendor app or reboot.' -ForegroundColor Yellow
