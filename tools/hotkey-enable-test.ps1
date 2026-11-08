# hotkey-enable-test.ps1 - tells the BIOS to send Fn key events: cmd 0x46 (CLEVO_CMD_SET_EVENTS_ENABLED, value 0),
# which the Linux Clevo driver sends on load and after every resume. Run it while C-Helper is running,
# then press Fn+Esc.
#
#   powershell -ExecutionPolicy Bypass -File .\hotkey-enable-test.ps1

$ErrorActionPreference = 'Stop'
$dll = @("$env:ProgramFiles\C-Helper\InsydeDCHU.dll", "$env:LOCALAPPDATA\CHelper\dchu\InsydeDCHU.dll") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dll) { throw "InsydeDCHU.dll not found" }
$copy = Join-Path $env:TEMP 'InsydeDCHU-test.dll'
Copy-Item $dll $copy -Force

Add-Type -TypeDefinition @"
using System.Runtime.InteropServices;
public static class HkTest {
    [DllImport(@"$copy")] public static extern int SetDCHU_Data(int cmd, byte[] data, int len);
}
"@

$r = [HkTest]::SetDCHU_Data(0x46, [byte[]](0x00, 0x00, 0x00, 0x00), 4)
Write-Host "Sent cmd 0x46 (enable events) -> $r"
Write-Host "Now press Fn+Esc (C-Helper should open) and Fn + numpad +/- (keyboard light)."
