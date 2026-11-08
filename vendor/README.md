# Vendor files (optional)

C-Helper uses two pieces from the laptop's own software:

| File | What it is | Without it |
|---|---|---|
| `InsydeDCHU.dll` | The vendor's signed library that sends commands to the BIOS | C-Helper copies it from the vendor "Fn hot keys and OSD" app on first run |
| `driver/` | The Clevo ACPI bridge driver (`ACPI\CLV0001`) the DLL talks to | It comes with the laptop's drivers and normally stays installed |

When the files are placed here:

- `InsydeDCHU.dll` is built **into** `CHelper.exe` and also installed next to it by the installer, so C-Helper works with no vendor software at all
- `driver/` is bundled by the installer and installed with `pnputil` on machines where the bridge device has no driver (e.g. after a clean Windows install)

Get them from your laptop with:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\export-vendor-files.ps1
```

or apply `0001-Bundle-vendor-files.patch` (adds `InsydeDCHU.dll`).

> [!WARNING]
> These files belong to their makers (Insyde / Clevo / Colorful) and are not covered by C-Helper's GPL licence.
> Check that you are allowed to before publishing them in a public repository or release.
