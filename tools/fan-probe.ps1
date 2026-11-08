# fan-probe.ps1 - READ-ONLY. Dumps the raw status block (extended 3) and the Clevo fan-info
# integers every 2 seconds, so they can be matched against the RPM / temps the vendor app shows.
#
# Usage: open the vendor Control Center on the System page (fan RPM visible), then run:
#   powershell -ExecutionPolicy Bypass -File .\fan-probe.ps1
# Halfway through, start something heavy (a game / Cinebench) so the numbers move.
# Output is also saved to fan-probe.txt next to this script.

param([string]$DllPath, [int]$Samples = 15)
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
public static class DchuFan {
    const string D = @"$local";
    [DllImport(D)] public static extern int GetDCHU_Data_Integer(int cmd, ref int data);
    [DllImport(D)] public static extern int SetDCHU_DataEx(int cmd, byte[] inBuf, int len, [In, Out] byte[] outBuf);
}
"@
function Int([int]$c) { $v = 0; [void][DchuFan]::GetDCHU_Data_Integer($c, [ref]$v); '{0:X8}' -f $v }

$log = Join-Path $PSScriptRoot 'fan-probe.txt'
"" | Set-Content $log
for ($i = 1; $i -le $Samples; $i++) {
    $in = New-Object byte[] 256; $in[0] = 3; $out = New-Object byte[] 256
    [void][DchuFan]::SetDCHU_DataEx(4, $in, 256, $out)
    $hex = ($out[0..63] | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
    $line = "{0:HH:mm:ss}  63={1} 64={2} 6E={3}`n   ex3[00-3F]: {4}" -f (Get-Date), (Int 0x63), (Int 0x64), (Int 0x6E), $hex
    Write-Host $line
    Add-Content $log $line
    Start-Sleep 2
}
Write-Host "`nSaved to $log" -ForegroundColor Yellow
