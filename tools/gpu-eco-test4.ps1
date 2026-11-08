# gpu-eco-test4.ps1 - replays the vendor's dGPU OFF/ON sequence (cc30wk.exe 0x0013) while
# staying in your current power mode. Must run as Administrator.
#
# Vendor sequence (from cc30wk.exe):
#   OFF: disable the NVIDIA Platform Controller (NVPCF) device, then extended command 34 = 0
#   ON : extended command 34 = 1, then re-enable the NVPCF device
# The NVPCF disable is NOT persistent (same flag the vendor uses), and ON always runs at the end.
#
# Before running: Entertainment mode, nothing using the NVIDIA GPU.
# Usage (admin PowerShell): powershell -ExecutionPolicy Bypass -File .\gpu-eco-test4.ps1

param([string]$DllPath)
$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Please run this from an Administrator PowerShell.'
}

$work = Join-Path $env:LOCALAPPDATA 'CHelperProbe'
New-Item -ItemType Directory -Force $work | Out-Null
if (-not $DllPath) {
    $pkg = Get-AppxPackage -AllUsers -Name '*FnhotkeysandOSD*' | Select-Object -First 1
    if (-not $pkg) { throw "Vendor package not found. Pass -DllPath to InsydeDCHU.dll" }
    $DllPath = Join-Path $pkg.InstallLocation 'FnKey\InsydeDCHU.dll'
}
$local = Join-Path $work 'InsydeDCHU.dll'
if (-not (Test-Path $local)) { Copy-Item $DllPath $local -Force }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Eco4 {
    const string D = @"$local";
    [DllImport(D)] public static extern int SetDCHU_DataEx(int cmd, byte[] inBuf, int len, [In, Out] byte[] outBuf);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] public static extern int CM_Locate_DevNodeW(out uint devInst, string id, int flags);
    [DllImport("cfgmgr32.dll")] public static extern int CM_Disable_DevNode(uint devInst, int flags);
    [DllImport("cfgmgr32.dll")] public static extern int CM_Enable_DevNode(uint devInst, int flags);
}
"@

function Ex34([byte]$v) {
    $in = New-Object byte[] 256; $in[0] = 34; $in[1] = $v; $out = New-Object byte[] 256
    $r = [Eco4]::SetDCHU_DataEx(4, $in, 256, $out)
    Write-Host ("  sent extended 34 = {0} (return {1})" -f $v, $r) -ForegroundColor DarkGray
}
function Show-Gpus([string]$title) {
    Write-Host "`n== $title ==" -ForegroundColor Cyan
    Get-PnpDevice -Class Display | Format-Table -AutoSize Status, FriendlyName | Out-String | Write-Host
}

# Locate NVPCF (NVIDIA Platform Controllers and Framework)
$nv = Get-PnpDevice -PresentOnly | Where-Object { ($_.HardwareID -join ' ') -match 'NVDA08' -or $_.FriendlyName -match 'Platform Controller' }
Write-Host "`n== NVIDIA platform devices found ==" -ForegroundColor Cyan
$nv | Format-Table -AutoSize Status, FriendlyName, InstanceId | Out-String | Write-Host
$pcf = $nv | Select-Object -First 1
if (-not $pcf) { throw 'No NVPCF device found - send me this output and stop here.' }
$dev = 0; $cr = [Eco4]::CM_Locate_DevNodeW([ref]$dev, $pcf.InstanceId, 0)
if ($cr -ne 0) { throw "CM_Locate_DevNode failed: $cr" }

Show-Gpus 'Before'
Read-Host 'Press Enter to run the vendor dGPU OFF sequence'
try {
    1..3 | ForEach-Object { [void][Eco4]::CM_Disable_DevNode($dev, 0); Start-Sleep -Milliseconds 100 }
    Write-Host '  NVPCF disabled (until reboot or re-enable)' -ForegroundColor DarkGray
    Start-Sleep -Milliseconds 100
    Ex34 0
    Write-Host 'Waiting 10 seconds...'; Start-Sleep 10
    Show-Gpus 'After dGPU OFF'
    Read-Host 'Note the NVIDIA status, then press Enter to turn it back ON'
}
finally {
    Ex34 1
    Start-Sleep -Milliseconds 100
    $e = [Eco4]::CM_Enable_DevNode($dev, 0)
    Write-Host "  NVPCF re-enabled (return $e)" -ForegroundColor DarkGray
    Write-Host 'Waiting 10 seconds...'; Start-Sleep 10
    Show-Gpus 'After dGPU ON'
}
Write-Host 'Done. If NVIDIA is still missing, switch power mode once in the vendor app, or reboot.' -ForegroundColor Yellow
