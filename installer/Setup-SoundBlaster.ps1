# Sets up Sound Blaster Studio on the Colorful EVOL P15 23 (Realtek ALC256, audio ID SUBSYS_155825E1).
# Run by the C-Helper installer and by the Sound page's "Set Up Sound Blaster" button, as administrator.
#
#   -CheckOnly   only report: exit 3 = already set up, 4 = needs setting up
#
# Exit codes: 0 = set up (restart needed), 1 = failed, 2 = not this laptop, 3 = already set up, 4 = needs setting up
#
# What it does, only on this audio chip:
#   1. Creates a restore point and backs up the current Realtek extension driver.
#   2. Adds the Creative version of the Realtek extension (HDX_ClevoExt_CT.inf), which sends the sound through
#      Creative's engine, and removes the plain one Windows Update installs (hdx_clevoext_rtk.inf), which would
#      otherwise win because it's newer.
#   3. Installs Speaker EQ Clevo (speaker tuning) and the Creative Audio Effects Component (the engine itself).
# It never touches any other driver.

param(
    [string]$Source = $PSScriptRoot,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$data = Join-Path $env:ProgramData 'CHelper\Sound'
New-Item -ItemType Directory $data -Force | Out-Null
$log = Join-Path $data 'setup-log.txt'

function Say([string]$text) {
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $text" | Out-File $log -Append -Encoding utf8
    Write-Output $text
}

function Run([string]$file, [string[]]$arguments) {
    $out = & $file @arguments 2>&1 | Out-String
    Say ("$file $($arguments -join ' ') -> exit $LASTEXITCODE`n" + $out.Trim())
}

try {
    $chip = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object InstanceId -match 'VEN_10EC&DEV_0256&SUBSYS_155825E1'
    if (-not $chip) { Say 'Not the EVOL P15 audio chip, nothing to do'; exit 2 }

    $drivers = Get-WindowsDriver -Online
    $creativeExt = @($drivers | Where-Object { $_.OriginalFileName -match '\\hdx_clevoext_ct\.inf$' })
    $plainExt = @($drivers | Where-Object { $_.OriginalFileName -match '\\hdx_clevoext_rtk\.inf$' })
    # Neither Creative installer registers in the installed-programs list, so look for what each one leaves behind:
    # Speaker EQ Clevo puts the speaker tuning in Windows, and the Audio Effects Component adds Creative's engine drivers.
    $hasEq = Test-Path (Join-Path $env:SystemRoot 'MBSpkrEQ.cfg')
    $hasEffects = @($drivers | Where-Object { $_.OriginalFileName -match '\\creativeextensionclevo\.inf' }).Count -gt 0 -and
                  @($drivers | Where-Object { $_.OriginalFileName -match '\\creativecomponent\.inf' }).Count -gt 0

    $needed = $creativeExt.Count -eq 0 -or $plainExt.Count -gt 0 -or -not $hasEq -or -not $hasEffects
    Say ("Creative extension: $($creativeExt.Count -gt 0), plain extension: $($plainExt.Count -gt 0), " +
         "Speaker EQ: $hasEq, Audio Effects: $hasEffects")
    if ($CheckOnly) { if ($needed) { exit 4 } else { exit 3 } }
    if (-not $needed) { Say 'Sound Blaster is already set up'; exit 3 }

    $ext = Join-Path $Source 'ExtRtk_9601.1\HDX_ClevoExt_CT.inf'
    $sbc = Join-Path $Source 'SBC'
    if (-not (Test-Path $ext)) { Say "Missing $ext"; exit 1 }

    Say 'Creating a restore point'
    try {
        Enable-ComputerRestore -Drive "$env:SystemDrive\"
        $key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
        New-ItemProperty $key -Name SystemRestorePointCreationFrequency -Value 0 -PropertyType DWord -Force | Out-Null
        Checkpoint-Computer -Description 'Before C-Helper set up Sound Blaster' -RestorePointType MODIFY_SETTINGS
        Remove-ItemProperty $key -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue
    } catch {
        Say "No restore point: $($_.Exception.Message) (the driver backup is still made)"
    }

    if ($plainExt.Count -gt 0) {
        $backup = Join-Path $data 'DriverBackup'
        New-Item -ItemType Directory $backup -Force | Out-Null
        foreach ($d in $plainExt) { Run pnputil @('/export-driver', $d.Driver, $backup) }
    }

    if ($creativeExt.Count -eq 0) { Run pnputil @('/add-driver', $ext, '/install') }
    foreach ($d in $plainExt) { Run pnputil @('/delete-driver', $d.Driver, '/uninstall', '/force') }
    Run pnputil @('/scan-devices')

    $silent = '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
    if (-not $hasEq) {
        $p = Start-Process (Join-Path $sbc 'SpeakerEQClevo_2.0.0.58.exe') -ArgumentList $silent -Wait -PassThru
        Say "Speaker EQ Clevo installer finished ($($p.ExitCode))"
    }
    if (-not $hasEffects) {
        $p = Start-Process (Join-Path $sbc 'CreativeAudioEffectsComponentInstaller_2.03.32.00.exe') -ArgumentList $silent -Wait -PassThru
        Say "Creative Audio Effects Component installer finished ($($p.ExitCode))"
    }

    Say 'Sound Blaster set up; a restart finishes it'
    exit 0
} catch {
    Say "Setting up Sound Blaster failed: $($_.Exception.Message)"
    exit 1
}
