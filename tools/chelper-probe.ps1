# chelper-probe.ps1 - READ-ONLY probe for the Clevo/Insyde DCHU interface.
# It only issues the same "get" queries the vendor Control Center makes at startup.
# Nothing is written to the BIOS, EC or the vendor app's settings.
#
# Usage:  powershell -ExecutionPolicy Bypass -File .\chelper-probe.ps1 [-DllPath <path to InsydeDCHU.dll>]

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
Copy-Item $DllPath $local -Force
Write-Host "Using DLL: $DllPath" -ForegroundColor DarkGray

$src = @"
using System;
using System.Runtime.InteropServices;
public static class Dchu {
    const string D = @"$local";
    [DllImport(D, CallingConvention = CallingConvention.Cdecl)] public static extern int GetDCHU_Data_Integer(int cmd, ref int data);
    [DllImport(D, CallingConvention = CallingConvention.Cdecl)] public static extern int SetDCHU_DataEx(int cmd, byte[] inBuf, int len, [In, Out] byte[] outBuf);
    [DllImport(D, CallingConvention = CallingConvention.Cdecl)] public static extern int ReadAppSettings(int hi, int lo, int len, [In, Out] byte[] buf);
}
"@
Add-Type -TypeDefinition $src

function Get-Int([int]$cmd) { $v = 0; $r = [Dchu]::GetDCHU_Data_Integer($cmd, [ref]$v); [pscustomobject]@{ Ret = $r; Value = $v } }
function Get-Ex([byte]$sub) { $in = New-Object byte[] 256; $in[0] = $sub; $out = New-Object byte[] 256; $r = [Dchu]::SetDCHU_DataEx(4, $in, 256, $out); [pscustomobject]@{ Ret = $r; Out = $out } }
function Get-Setting([int]$id, [int]$len = 1) { $b = New-Object byte[] ([Math]::Max($len,1)); [void][Dchu]::ReadAppSettings([int][Math]::Floor($id / 256), $id % 256, $len, $b); $b }

Write-Host "`n== GPU switch (DataEx 21) ==" -ForegroundColor Cyan
$g = Get-Ex 21
$names = @{ 1 = 'Integrated GPU Only'; 2 = 'Discrete GPU Only'; 3 = 'MSHybrid'; 4 = 'Dynamic' }
$cur = $g.Out[0]; $mask = $g.Out[1]
"Return code      : $($g.Ret)"
"Current mode     : $cur ($($names[[int]$cur]))"
"Supported mask   : 0x{0:X2}" -f $mask
foreach ($bit in 0..3) { "  {0,-20} {1}" -f $names[$bit + 1], $(if ($mask -band (1 -shl $bit)) { 'supported' } else { '-' }) }
"Raw              : " + (($g.Out[0..7] | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')

Write-Host "`n== BIOS feature flags ==" -ForegroundColor Cyan
foreach ($c in 0x52, 0x7A) { $x = Get-Int $c; "cmd 0x{0:X2}: ret={1} value=0x{2:X8}" -f $c, $x.Ret, $x.Value }

Write-Host "`n== Vendor app settings ==" -ForegroundColor Cyan
"Power profile (257)    : $((Get-Setting 257)[0])"
"Fan mode (1029)        : $((Get-Setting 1029)[0])"
"Turn off dGPU (1032)   : $((Get-Setting 1032)[0])"
"Power Saving bit7 (288): $((Get-Setting 288)[0])"

Write-Host "`n== Display adapters ==" -ForegroundColor Cyan
Get-PnpDevice -Class Display | Format-Table -AutoSize Status, FriendlyName
