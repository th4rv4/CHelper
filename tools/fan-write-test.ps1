# fan-write-test.ps1 - checks whether the EC accepts direct fan speed commands (Clevo 0x68 / 0x69),
# which C-Helper needs for custom fan curves.
#
# What it does (about 40 seconds, fans will get loud for a bit):
#   1. Reads fan duty for 4 s (baseline)
#   2. Sets both fans to 100% with cmd 0x68 and reads for 8 s
#   3. Sets both fans to 40% and reads for 8 s
#   4. Hands control back with cmd 0x69 and fan mode Auto (0x79 sub 0x01 = 0), reads for 8 s
#
# Quit C-Helper first, then run as admin:
#   powershell -ExecutionPolicy Bypass -File .\fan-write-test.ps1
# Output is also saved to fan-write-test.txt next to this script.

param([string]$DllPath)
$ErrorActionPreference = 'Stop'
if (-not $DllPath) {
    $DllPath = Join-Path $env:LOCALAPPDATA 'CHelper\dchu\InsydeDCHU.dll'
    if (-not (Test-Path $DllPath)) { throw "InsydeDCHU.dll not found. Run C-Helper once, or pass -DllPath" }
}
$work = Join-Path $env:LOCALAPPDATA 'CHelperProbe'
New-Item -ItemType Directory -Force $work | Out-Null
$local = Join-Path $work 'InsydeDCHU.dll'
Copy-Item $DllPath $local -Force

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class FanW {
    const string D = @"$local";
    [DllImport(D)] public static extern int GetDCHU_Data_Integer(int cmd, ref int data);
    [DllImport(D)] public static extern int SetDCHU_Data(int cmd, byte[] data, int len);
}
"@

$log = Join-Path $PSScriptRoot 'fan-write-test.txt'
"" | Set-Content $log
function Say($s) { Write-Host $s; Add-Content $log $s }
function Fan([int]$cmd) {
    $v = 0; [void][FanW]::GetDCHU_Data_Integer($cmd, [ref]$v)
    $duty = [math]::Round(($v -band 0xFF) * 100 / 255); $temp = ($v -shr 16) -band 0xFF
    "{0}% {1}C" -f $duty, $temp
}
function Watch([string]$label, [int]$secs) {
    for ($i = 0; $i -lt $secs; $i++) {
        Say ("{0:HH:mm:ss} {1,-10} CPU {2,-9} GPU {3}" -f (Get-Date), $label, (Fan 0x63), (Fan 0x64))
        Start-Sleep 1
    }
}
function Write4([int]$cmd, [int]$value) {
    $r = [FanW]::SetDCHU_Data($cmd, [BitConverter]::GetBytes($value), 4)
    Say ("Write cmd=0x{0:X2} value=0x{1:X8} -> {2}" -f $cmd, $value, $r)
}

try {
    Watch 'baseline' 4
    # 0x68: fan1 | fan2 << 8 | fan3 << 16, duty 0-255
    Write4 0x68 (255 -bor (255 -shl 8) -bor (255 -shl 16))
    Watch '100%' 8
    $d = [int](0.40 * 255)
    Write4 0x68 ($d -bor ($d -shl 8) -bor ($d -shl 16))
    Watch '40%' 8
}
finally {
    Write4 0x69 0                                   # fans back to automatic
    Write4 0x79 ((0x01 -shl 24) -bor 0)             # fan mode Auto
    Watch 'restored' 8
    Say "`nSaved to $log"
}
