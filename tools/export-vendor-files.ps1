# export-vendor-files.ps1 - copies the two vendor pieces C-Helper relies on into the repo's vendor\ folder,
# so builds and the installer no longer need the vendor app:
#   vendor\InsydeDCHU.dll   from the "Fn hot keys and OSD" app (or C-Helper's own saved copy)
#   vendor\driver\          the Clevo ACPI bridge driver (ACPI\CLV0001), exported from Windows' driver store
#
# Run from an admin PowerShell in the repo folder:
#   powershell -ExecutionPolicy Bypass -File .\tools\export-vendor-files.ps1

$ErrorActionPreference = 'Stop'
# Inside the repo (tools\) write to the repo's vendor\; run on its own, write next to the script.
$repo = Split-Path $PSScriptRoot -Parent
if (-not $repo -or -not (Test-Path (Join-Path $repo 'src\CHelper.csproj'))) { $repo = $PSScriptRoot }
$vendor = Join-Path $repo 'vendor'
New-Item -ItemType Directory -Force $vendor | Out-Null

# 1) InsydeDCHU.dll
$dll = $null
$pkg = Get-AppxPackage -Name '*FnhotkeysandOSD*' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pkg) {
    $p = Join-Path $pkg.InstallLocation 'FnKey\InsydeDCHU.dll'
    if (Test-Path $p) { $dll = $p }
}
if (-not $dll) {
    $p = Join-Path $env:LOCALAPPDATA 'CHelper\dchu\InsydeDCHU.dll'
    if (Test-Path $p) { $dll = $p }
}
if ($dll) {
    Copy-Item $dll (Join-Path $vendor 'InsydeDCHU.dll') -Force
    Write-Host "InsydeDCHU.dll  <- $dll" -ForegroundColor Green
} else {
    Write-Host "InsydeDCHU.dll not found (vendor app removed and C-Helper never ran?)" -ForegroundColor Yellow
}

# 2) Clevo ACPI bridge driver
$drv = Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceID -like 'ACPI\CLV0001*' } | Select-Object -First 1
if ($drv -and $drv.InfName) {
    $out = Join-Path $vendor 'driver'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    New-Item -ItemType Directory -Force $out | Out-Null
    & pnputil.exe /export-driver $drv.InfName $out | Out-Host
    Write-Host ("Bridge driver   <- {0} ({1} {2})" -f $drv.InfName, $drv.DeviceName, $drv.DriverVersion) -ForegroundColor Green
} else {
    Write-Host "Clevo ACPI bridge device (ACPI\CLV0001) not found or has no driver" -ForegroundColor Yellow
}

Write-Host "`nvendor\ now contains:"
Get-ChildItem $vendor -Recurse -File | ForEach-Object { Write-Host ("  {0}  {1:N0} bytes" -f $_.FullName, $_.Length) }
Write-Host "`nNext: git add vendor && git commit -m ""Bundle vendor files""  (or build locally without committing)"
