# gpu-eco-test.ps1 - tests whether BIOS command 0x4F turns the NVIDIA dGPU off/on
# independently of the power profile.
#
# It sends only the two values the vendor app already sends on every mode change:
#   0x4F = 1  (sent by the vendor in Power Saving)   -> expected: dGPU off
#   0x4F = 2  (sent by the vendor in Entertainment)  -> expected: dGPU back on
# The power profile is NOT touched. The script always restores "2" before exiting.
#
# Before running: be in Entertainment mode, close games and anything using the NVIDIA GPU.
# Usage: powershell -ExecutionPolicy Bypass -File .\gpu-eco-test.ps1

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
public static class DchuW {
    const string D = @"$local";
    [DllImport(D)] public static extern int SetDCHU_Data(int cmd, byte[] data, int len);
}
"@

function Set-GpuPower([int]$v) {
    $b = [BitConverter]::GetBytes($v)
    $r = [DchuW]::SetDCHU_Data(0x4F, $b, $b.Length)
    Write-Host ("  sent 0x4F = {0}  (return {1})" -f $v, $r) -ForegroundColor DarkGray
}
function Show-Gpus([string]$title) {
    Write-Host "`n== $title ==" -ForegroundColor Cyan
    Get-PnpDevice -Class Display | Format-Table -AutoSize Status, FriendlyName | Out-String | Write-Host
}

Show-Gpus 'Before'
Read-Host 'Press Enter to send "dGPU off" (0x4F = 1)'
try {
    Set-GpuPower 1
    Write-Host 'Waiting 8 seconds...'; Start-Sleep 8
    Show-Gpus 'After 0x4F = 1'
    Read-Host 'Note the NVIDIA status above, then press Enter to turn it back on'
}
finally {
    Set-GpuPower 2
    Write-Host 'Waiting 8 seconds...'; Start-Sleep 8
    Show-Gpus 'After 0x4F = 2 (restored)'
}
Write-Host 'Done. If NVIDIA still shows Unknown, switch power mode once in the vendor app or reboot.' -ForegroundColor Yellow
