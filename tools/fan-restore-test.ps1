# fan-restore-test.ps1 - finds the command that hands the fans back to the EC after a direct
# fan speed write (cmd 0x68). Run it while the fans are still stuck at 40% after fan-write-test.ps1.
#
# It tries one candidate at a time, then watches for 8 s. As soon as the fan duty moves away from the
# fixed 40% (or changes on its own), it stops and reports which step worked.
#
# Quit C-Helper first, then from an admin PowerShell:
#   powershell -ExecutionPolicy Bypass -File .\fan-restore-test.ps1
# Output is also saved to fan-restore-test.txt next to this script.

param([string]$DllPath, [int]$Stuck = 40)
$ErrorActionPreference = 'Stop'
if (-not $DllPath) {
    $DllPath = Join-Path $env:LOCALAPPDATA 'CHelper\dchu\InsydeDCHU.dll'
    if (-not (Test-Path $DllPath)) { throw "InsydeDCHU.dll not found. Run C-Helper once, or pass -DllPath" }
}
$work = Join-Path $env:LOCALAPPDATA 'CHelperProbe'
New-Item -ItemType Directory -Force $work | Out-Null
$local = Join-Path $work 'InsydeDCHU2.dll'
Copy-Item $DllPath $local -Force

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class FanR {
    const string D = @"$local";
    [DllImport(D)] public static extern int GetDCHU_Data_Integer(int cmd, ref int data);
    [DllImport(D)] public static extern int SetDCHU_Data(int cmd, byte[] data, int len);
}
"@

$log = Join-Path $PSScriptRoot 'fan-restore-test.txt'
"" | Set-Content $log
function Say($s) { Write-Host $s; Add-Content $log $s }
function Duty([int]$cmd) { $v = 0; [void][FanR]::GetDCHU_Data_Integer($cmd, [ref]$v); [math]::Round(($v -band 0xFF) * 100 / 255) }
function Temp([int]$cmd) { $v = 0; [void][FanR]::GetDCHU_Data_Integer($cmd, [ref]$v); ($v -shr 16) -band 0xFF }
function Write4([int]$cmd, [int]$value) {
    $r = [FanR]::SetDCHU_Data($cmd, [BitConverter]::GetBytes($value), 4)
    Say ("  Write cmd=0x{0:X2} value=0x{1:X8} -> {2}" -f $cmd, $value, $r)
}
function Released([string]$label) {
    for ($i = 0; $i -lt 8; $i++) {
        Start-Sleep 1
        $c = Duty 0x63; $g = Duty 0x64
        Say ("  {0:HH:mm:ss} CPU {1}% {2}C  GPU {3}% {4}C" -f (Get-Date), $c, (Temp 0x63), $g, (Temp 0x64))
        if ($c -ne $Stuck -or $g -ne $Stuck) { Say "==> Fans released after: $label"; return $true }
    }
    return $false
}

Say ("Start: CPU {0}%  GPU {1}%" -f (Duty 0x63), (Duty 0x64))
if ((Duty 0x63) -ne $Stuck -and (Duty 0x64) -ne $Stuck) { Say "Fans are not at $Stuck% - nothing to test (already automatic?)"; return }

$steps = @(
    @{ L = 'A: 0x69 = 0x0F (auto, all fans)';       A = { Write4 0x69 0x0F } },
    @{ L = 'B: 0x69 = 0x07';                         A = { Write4 0x69 0x07 } },
    @{ L = 'C: 0x69 = 0xFFFFFF';                     A = { Write4 0x69 0xFFFFFF } },
    @{ L = 'D: fan mode Max, then Auto';             A = { Write4 0x79 ((0x01 -shl 24) -bor 1); Start-Sleep 2; Write4 0x79 ((0x01 -shl 24) -bor 0) } },
    @{ L = 'E: power profile re-applied (Entertainment)'; A = { Write4 0x79 ((0x19 -shl 24) -bor 3) } }
)
foreach ($s in $steps) {
    Say "`nTrying $($s.L)"
    & $s.A
    if (Released $s.L) { Say "`nSaved to $log"; return }
}
Say "`nNone of the steps released the fans. Restart the laptop to get automatic control back."
Say "Saved to $log"
