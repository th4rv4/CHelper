# dynamic-boost-test.ps1 - checks whether turning off the "NVIDIA Platform Controllers and Framework"
# device (only that, the GPU stays on) disables Dynamic Boost (100 W -> 70 W max).
# The device is always switched back on at the end, even if something fails.
#
# Use Standard GPU mode (dGPU awake), quit C-Helper, then from an admin PowerShell:
#   powershell -ExecutionPolicy Bypass -File .\dynamic-boost-test.ps1
# Output is saved to dynamic-boost-test.txt next to this script.

$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'dynamic-boost-test.txt'
"" | Set-Content $log
function Say($s) { Write-Host $s; Add-Content $log $s }
$id = 'ACPI\NVDA0820\NPCF'
$smi = "$env:WINDIR\System32\nvidia-smi.exe"
if (-not (Test-Path $smi)) { $smi = (Get-Command nvidia-smi -ErrorAction Stop).Source }

function Snapshot($label) {
    Say "--- $label ---"
    $dev = Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue
    Say ("NPCF device: {0}" -f $(if ($dev) { $dev.Status } else { 'not found' }))
    $gpu = Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' }
    Say ("NVIDIA GPU present: {0}" -f [bool]$gpu)
    & $smi --query-gpu=power.draw,power.limit,enforced.power.limit,power.default_limit,power.max_limit --format=csv 2>&1 | ForEach-Object { Say "  $_" }
    & $smi -q -d POWER 2>&1 | Select-String 'Current Power Limit|Default Power Limit|Max Power Limit|Base Power' | ForEach-Object { Say "  $($_.Line.Trim())" }
}

try {
    Snapshot 'before (Dynamic Boost on)'
    Say "`nTurning the NPCF device off..."
    Disable-PnpDevice -InstanceId $id -Confirm:$false
    Start-Sleep 5
    Snapshot 'NPCF off'
}
finally {
    Say "`nTurning the NPCF device back on..."
    Enable-PnpDevice -InstanceId $id -Confirm:$false
    Start-Sleep 5
    Snapshot 'after (restored)'
    Say "`nSaved to $log"
}
