# gpu-eco-test3.ps1 - replays the vendor's own Power Saving values step by step,
# to see (a) whether our writes reach the BIOS and (b) which step cuts the dGPU.
#
#   step 1: profile 0x79/0x19 = 1      (vendor Power Saving profile)
#   step 2: command 0x4F      = 1      (vendor sends this too in Power Saving)
#   restore: 0x4F = 2, profile = 3     (vendor Entertainment values) - always runs
#
# Before running: select Entertainment in the vendor app, close games / anything on the NVIDIA GPU.
# Usage: powershell -ExecutionPolicy Bypass -File .\gpu-eco-test3.ps1

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
public static class DchuT3 {
    const string D = @"$local";
    [DllImport(D)] public static extern int SetDCHU_Data(int cmd, byte[] data, int len);
}
"@

function Send([int]$cmd, [int]$value, [int]$sub = -1) {
    $b = [BitConverter]::GetBytes([int]$value)
    if ($sub -ge 0) { $b[3] = [byte]$sub }
    $r = [DchuT3]::SetDCHU_Data($cmd, $b, $b.Length)
    Write-Host ("  sent cmd 0x{0:X2} sub {1} value {2} (return {3})" -f $cmd, $sub, $value, $r) -ForegroundColor DarkGray
}
function Show-Gpus([string]$title) {
    Write-Host "`n== $title ==" -ForegroundColor Cyan
    Get-PnpDevice -Class Display | Format-Table -AutoSize Status, FriendlyName | Out-String | Write-Host
}

Show-Gpus 'Before (Entertainment)'
try {
    Read-Host 'Step 1 - press Enter to send profile = 1 (Power Saving)'
    Send 0x79 1 0x19
    Write-Host 'Waiting 10 seconds... (listen for the fans / watch the power LED)'; Start-Sleep 10
    Show-Gpus 'After step 1 (profile 1)'

    Read-Host 'Step 2 - press Enter to also send 0x4F = 1'
    Send 0x4F 1
    Write-Host 'Waiting 10 seconds...'; Start-Sleep 10
    Show-Gpus 'After step 2 (profile 1 + 0x4F 1)'
    Read-Host 'Press Enter to restore Entertainment'
}
finally {
    Send 0x4F 2
    Send 0x79 3 0x19
    Write-Host 'Waiting 10 seconds...'; Start-Sleep 10
    Show-Gpus 'Restored (Entertainment)'
}
Write-Host 'Done. If anything looks off, pick Entertainment again in the vendor app or reboot.' -ForegroundColor Yellow
