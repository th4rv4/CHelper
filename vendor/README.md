# Vendor files

C-Helper uses two pieces from the laptop's own software:

| File | What it is | How C-Helper uses it |
|---|---|---|
| `InsydeDCHU.dll` | The vendor's signed library that sends commands to the BIOS (from the "Fn hot keys and OSD" app, `FnKey\InsydeDCHU.dll`) | Built into `CHelper.exe` and installed next to it, so no vendor app is needed |
| `sound/` | Sound Blaster Studio drivers from the Colorful EVOL P15 driver page (June 2024): `ExtRtk_9601.1/` is the Realtek audio extension (Creative version, `HDX_ClevoExt_CT.inf`) from the **Audio** package, and `SBC/` holds Speaker EQ Clevo and the Creative Audio Effects Component from the **SBC** package | Installed with C-Helper and set up by `installer/Setup-SoundBlaster.ps1` on the EVOL P15's audio chip, and again from the Sound page if Windows Update replaces the driver |
| `wifi/` | Intel Wi-Fi driver 24.80.0.2 for the AX210 / AX211 card (`netwtw6e.inf`), the version that works on the EVOL P15 | Installed with C-Helper. **Wi-Fi Driver Refresh** reinstalls it until C-Helper has saved its own copy of the working driver, and setup installs it when the Wi-Fi card has no working driver (e.g. after a clean Windows install) |
| `driver/` | The Clevo ACPI bridge driver (`ACPI\CLV0001`) the DLL talks to | Bundled by the installer and installed with `pnputil` when the laptop is missing it (e.g. after a clean Windows install) |

The DLL is included in the repo. Add the bridge driver (and refresh the DLL) from your laptop with:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\export-vendor-files.ps1
git add vendor
git commit -m "Add Clevo ACPI bridge driver"
```

If the vendor app is gone, the script falls back to C-Helper's own saved copy of the DLL.

These files belong to their makers (Insyde / Clevo / Colorful / Realtek / Creative / Intel), not to the developer of C-Helper, and are not covered by C-Helper's GPL licence. They are included only so the laptop's own hardware keeps working; C-Helper is completely non-profit.
