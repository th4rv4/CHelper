# advanced-probe.ps1 - READ-ONLY. Checks what the advanced C-Helper features can use on this laptop:
#   - CPU power limits: is the Intel XTU service there, and does Windows Memory Integrity block it?
#   - GPU: NVIDIA power limits / Dynamic Boost (via nvidia-smi) and the NVIDIA platform device
#   - Sound Boost: is the vendor's audio enhancement installed (C:\SoundBoost)?
#   - Touchpad: the state Windows reports
# Nothing is changed. Run from an admin PowerShell:
#   powershell -ExecutionPolicy Bypass -File .\advanced-probe.ps1
# Output is saved to advanced-probe.txt next to this script.

$log = Join-Path $PSScriptRoot 'advanced-probe.txt'
"" | Set-Content $log
function Say($s) { Write-Host $s; Add-Content $log $s }
function Section($t) { Say ""; Say "=== $t ===" }

Section "System"
$cs = Get-CimInstance Win32_ComputerSystem
$bios = Get-CimInstance Win32_BIOS
Say ("Model: {0} {1}  BIOS: {2} ({3})" -f $cs.Manufacturer, $cs.Model, $bios.SMBIOSBIOSVersion, $bios.ReleaseDate)
Say ("CPU: {0}" -f (Get-CimInstance Win32_Processor).Name)

Section "CPU power limits (Intel XTU)"
$xtu = Get-Service | Where-Object { $_.Name -match 'xtu' -or $_.DisplayName -match 'Extreme Tuning|XTU' }
if ($xtu) { $xtu | ForEach-Object { Say ("Service: {0} ({1}) - {2}" -f $_.Name, $_.DisplayName, $_.Status) } }
else { Say "No Intel XTU service installed" }
try {
    $dg = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction Stop
    $running = @($dg.SecurityServicesRunning)
    Say ("Memory Integrity (HVCI) running: {0}" -f ($running -contains 2))
    Say ("Virtualization-based security status: {0}" -f $dg.VirtualizationBasedSecurityStatus)
} catch { Say "Device Guard info not available: $($_.Exception.Message)" }
$hvci = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -ErrorAction SilentlyContinue
if ($hvci) { Say ("HVCI registry Enabled = {0}" -f $hvci.Enabled) }
Say ("Hyper-V / VM platform: " + ((Get-CimInstance Win32_ComputerSystem).HypervisorPresent))

Section "GPU (nvidia-smi)"
$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if (-not $smi) { $p = "$env:WINDIR\System32\nvidia-smi.exe"; if (Test-Path $p) { $smi = $p } }
if ($smi) {
    & $smi --query-gpu=name,driver_version,power.draw,power.limit,power.default_limit,power.min_limit,power.max_limit,clocks.gr,clocks.max.gr,clocks.mem,temperature.gpu --format=csv 2>&1 | ForEach-Object { Say $_ }
    Say ""
    & $smi -q -d POWER 2>&1 | Select-Object -First 40 | ForEach-Object { Say $_ }
} else { Say "nvidia-smi not found (dGPU off in Eco? switch to Standard and run again)" }

Section "NVIDIA platform device (Dynamic Boost)"
Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'NVDA0820|NPCF' -or $_.FriendlyName -match 'Platform Controllers|NVPCF' } |
    ForEach-Object { Say ("{0} | {1} | {2}" -f $_.FriendlyName, $_.Status, $_.InstanceId) }
Get-Service | Where-Object { $_.Name -match 'NVDisplay|NvContainer|nvpcf' } | ForEach-Object { Say ("Service: {0} - {1}" -f $_.Name, $_.Status) }

Section "Sound Boost"
if (Test-Path 'C:\SoundBoost') {
    Get-ChildItem 'C:\SoundBoost' | Select-Object -First 30 | ForEach-Object { Say ("  {0}  {1}" -f $_.Name, $_.Length) }
    if (Test-Path 'C:\SoundBoost\config.txt') { Say "config.txt:"; Get-Content 'C:\SoundBoost\config.txt' | Select-Object -First 20 | ForEach-Object { Say "  $_" } }
} else { Say "C:\SoundBoost not installed" }
Get-ChildItem 'HKLM:\SOFTWARE\EqualizerAPO' -ErrorAction SilentlyContinue | ForEach-Object { Say "Registry: $($_.Name)" }

Section "Touchpad"
$tp = Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status' -ErrorAction SilentlyContinue
Say ("PrecisionTouchPad Status Enabled = {0}" -f $(if ($tp) { $tp.Enabled } else { 'not present' }))

Say "`nSaved to $log"
