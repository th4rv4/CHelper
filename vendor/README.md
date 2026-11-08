# Vendor files

C-Helper uses two pieces from the laptop's own software:

| File | What it is | How C-Helper uses it |
|---|---|---|
| `InsydeDCHU.dll` | The vendor's signed library that sends commands to the BIOS (from the "Fn hot keys and OSD" app, `FnKey\InsydeDCHU.dll`) | Built into `CHelper.exe` and installed next to it, so no vendor app is needed |
| `driver/` | The Clevo ACPI bridge driver (`ACPI\CLV0001`) the DLL talks to | Bundled by the installer and installed with `pnputil` when the laptop is missing it (e.g. after a clean Windows install) |

The DLL is included in the repo. Add the bridge driver (and refresh the DLL) from your laptop with:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\export-vendor-files.ps1
git add vendor
git commit -m "Add Clevo ACPI bridge driver"
```

If the vendor app is gone, the script falls back to C-Helper's own saved copy of the DLL.

These files belong to their makers (Insyde / Clevo / Colorful) and are not covered by C-Helper's GPL licence.
