# Clevo / Insyde DCHU interface: Colorful EVOL P15 23

Reverse engineered from the vendor package `CLEVOCO.FnhotkeysandOSD` 7.101 (CC40 7.94.1.0)
and checked on the laptop (i7-13620H, RTX 4060).

## Transport

`InsydeDCHU.dll` opens the Clevo ACPI bridge (device interface `86994c74-ad43-4812-b7e7-0c420b5c5fd7`)
and sends IOCTL `0x322400`, which evaluates `_DSM` with GUID `93f224e4-fbdc-4bbf-add6-db71bdc0afad`.
That is the same `_DSM` the Linux `tuxedo-drivers` `clevo_acpi` driver uses.

| Call type    | Export                                            |
|--------------|---------------------------------------------------|
| Read         | `GetDCHU_Data_Integer(cmd, ref int)`              |
| Write        | `SetDCHU_Data(cmd, byte[4], 4)`                   |
| Package_Read | `GetDCHU_Data_Buffer(cmd, byte[256])`             |
| Package_RW   | `SetDCHU_DataEx(4, in[256], 256, out[256])`; `in[0]` = sub-command |

Writes that carry a sub-command pack the value in the low bytes and the sub-command in byte 3.

## Commands

| Feature | Call | Values | Status |
|---|---|---|---|
| Power profile | Write `0x79`, sub `0x19` | Quiet 0, Power Saving 1, Performance 2, Entertainment 3 | confirmed (vendor log) |
| Fan mode | Write `0x79`, sub `0x01` | Auto 0, Max 1, MaxQ 5, Custom 6, Noiseless 8, IQST 9 | from vendor code |
| Keyboard colour (1 zone) | Write `0x67`, sub `0xF0` | `B<<16 \| R<<8 \| G` | confirmed |
| Keyboard brightness | Write `0x67`, sub `0xF4` | 0–255 | confirmed |
| Webcam | Write `0x22` | 0 / 1 | from vendor code + tuxedo |
| MUX read | Package_RW sub 21 | out[0] = current, out[1] = supported mask (bit n = mode n+1) | confirmed: current 3, mask 0x06 |
| MUX write | Package_RW sub 22, in[1] = mode | 1 iGPU only, 2 dGPU only, 3 MSHybrid, 4 Dynamic; restart needed | from vendor code |
| dGPU power | Package_RW sub 34, in[1] = 0 / 1 | together with NVPCF (`ACPI\NVDA0820\NPCF`) disable / enable | confirmed |
| Charge limit read | Package_RW sub 30 | out[0] enabled, out[1] start, out[2] stop, out[16..31] allowed starts, out[32..47] allowed stops | from vendor code |
| Charge limit write | Package_RW sub 31, in = [enabled, start, stop] | Maximum = [0, highest start, 100], Recommended = [1, 70, 80] | from vendor code |
| Fn-key events | open `ACPI#CLV0001` interface, IOCTL `0x322404` (register event handle), wait, IOCTL `0x322408` → 8-byte code | Fn+Esc = 149 | from vendor PCC.exe |
| Fan duty | Write `0x68`, value = fan1 \| fan2 << 8 \| fan3 << 16 (0–255 each) | EC holds the duty until released | confirmed |
| Fan release | Write `0x69`, value `0x0F` | all fans back to EC control (value 0 does **not** release) | confirmed |
| Enable key events | Write `0x46`, value 0 | BIOS only sends Fn key events after this; forgotten on sleep, so send at startup and on resume (same as Linux `CLEVO_CMD_SET_EVENTS_ENABLED`) | confirmed |
| Lock state events | Hotkey events `0xC7` / `0xC8` | Num Lock off / on (sent with every press) | confirmed |
| | Hotkey events `0xC9` / `0xCA` | Caps Lock off / on | confirmed |
| Keyboard backlight timeout | Write `0x79`, sub `0x18` | value = seconds << 8 \| 0xFF; 0 = never (vendor default 30 s) | from vendor code |
| CPU fan / temp | Read `0x63` | byte0 = duty 0–255, byte2 = °C | confirmed |
| GPU fan / temp | Read `0x64` | byte0 = duty 0–255, byte2 = °C (meaningless while dGPU is off) | confirmed |
| Fan RPM | not exposed; vendor appears to scale it from duty (~82 RPM per %) | — | — |
| BIOS features | Read `0x52`, `0x7A` | `0x04290025`, `0x60020053` | read only |

### dGPU off / on (vendor `cc30wk.exe 0x0013`)

- **Off:** `CM_Disable_DevNode(NVPCF, 0)` three times, 100 ms apart, then Package_RW sub 34 = 0.
- **On:** Package_RW sub 34 = 1, then `CM_Enable_DevNode(NVPCF, 0)`.

This works independently of the power profile; tested in Entertainment.

## Vendor logs

The vendor apps log every call to the Windows event logs `OutLog` and `PowerBiosServerLog`. Read them with:

```powershell
Get-WinEvent -FilterHashtable @{ LogName='OutLog','PowerBiosServerLog'; StartTime=(Get-Date).AddMinutes(-3) } | sort TimeCreated | ft TimeCreated, Message -Wrap
```
