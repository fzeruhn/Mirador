# Mirador's direct-mode layer

Run an NVIDIA direct-mode headset on Windows with [Monado](https://monado.freedesktop.org/), through a Vulkan layer.
Built for the Pimax Dream Air; other Pimax headsets get the mode their EDID asks for, untested.

The layer ships inside Mirador (since 2026-11-09): Mirador's installer registers it, and Mirador's Start turns it on for
a headset that uses NVIDIA direct mode. Until then it was released on its own as `dreamair-vk-layer`, with install and
start scripts; they are gone, and this page says what Mirador does in their place.

**Renamed (2026-11-09):** the layer is `VK_LAYER_MIRADOR_direct_mode` (was `VK_LAYER_DREAMAIR_nvapi_direct`), its files
`mirador_direct_mode.dll`/`.json` and `mirador-display-check.exe`, its variables `MIRADOR_DIRECT_MODE*` (were
`DREAMAIR_VK_LAYER*`), its log folder `%LOCALAPPDATA%\Mirador\direct-mode`. For one release the layer still reads each
of its variables under its old name when the new one isn't set, and the guard still lets the check tool's old name in.
Mirador uses only the layer it comes with (a profile can no longer name a layer folder). The Vulkan loader reads one
switch per layer: the renamed layer is switched on by `MIRADOR_DIRECT_MODE=1` only.

Status: version 0.1.0. Developed against one machine (RTX 5070 Ti, NVIDIA driver 621.01). Other drivers and GPUs are
untried; the check tool below tells you whether yours works.

## Which headsets

The layer looks for the direct-mode displays of each vendor in its table: Pimax (`PVR`, NVAPI id `0xd242`) for now.
Another vendor needs a capture on its headset first.

- **Tested:** the Pimax Dream Air (`PVR 0x1019`), 7104x3840 at 90 Hz: its ids, with an EDID that asks for that mode
  or for none. Pimax gives other headsets the same ids (the P2 8K Plus); one whose EDID asks for another mode is
  untested, below.
- **Untested, from its EDID:** any other headset of a vendor in the table. Its mode is the one its EDID asks for: the
  DisplayID timing marked preferred (Type I or VII), else the base block's first detailed timing, else the first
  DisplayID timing; a size over 16384 or a rate outside 24 to 500 Hz is no mode. The layer logs that it is untested.
  Only an EDID read from the display through NVAPI counts: the registry keeps every Pimax display Windows ever saw, so
  an untested headset's EDID from there is refused. Tracking and distortion still need a Monado driver for it.
- **Left to Windows:** an untested headset whose EDID carries Microsoft's HMD block (OUI `5C-12-CA`), which Windows' own
  HMD route drives. Never the Dream Air, even when an EDID override adds that block. The check tool's verdict for it is
  `NOT USED: …` (exit code 3), neither a pass nor a fail; Mirador then starts Monado anyway and warns that only a
  Monado with Windows' own headset route (upstream !2811) can show an image on it.
- **No EDID:** with no EDID readable, one display of the vendor and the Dream Air's board on USB, the display is taken to
  be a Dream Air (the board assumption is the Dream Air's only).
- The board's heartbeat is Monado's (its Dream Air driver): the layer sends none. The check tool sends the Dream Air's.

## What it is

On Windows, NVIDIA's driver hides the Dream Air from the desktop ("direct mode"). The only way to show an image on it is
a set of private NVIDIA driver calls, the ones Pimax and SteamVR use. This project is an **implicit Vulkan layer**,
`VK_LAYER_MIRADOR_direct_mode`, that makes the headset appear to Monado as an ordinary `VK_KHR_display` display and
sends Monado's frames to it through those calls. Monado needs no Dream Air display code of its own for this: it uses its
generic `vk_display` output.

What Mirador installs, in `{app}\layer` (`C:\Program Files\Mirador\layer`):

| File | What |
|---|---|
| `mirador_direct_mode.dll`, `mirador_direct_mode.json` | The layer and its manifest |
| `mirador-display-check.exe` | Checks your driver and the headset without Monado |

What the layer itself does not do: build or ship Monado, stop Pimax, switch your OpenXR runtime, or upload the eye
tracker's geometry. Mirador and its headset module do those.
It works in Monado and the check tool; it is not loaded by any other program (see "Never set `MIRADOR_DIRECT_MODE`
machine-wide").

## What you need

- Windows 10 or 11, 64-bit, and an **NVIDIA** GPU. AMD, Intel, other headsets and other operating systems are not
  supported.
- The Dream Air connected by DisplayPort and USB.
- No Visual C++ Redistributable: the layer and the check tool carry their own C++ runtime.
- A **Monado build with the Dream Air drivers and the Windows patches**, made the active OpenXR runtime. For now that is
  Finn's `windows` branch of `monado-dreamair`; this project does not ship Monado.
- **Pimax stopped**: Pimax Play quit (tray icon > Exit) and its service stopped, and SteamVR and OVR Lighthouse Manager
  closed. They hold the display, the headset's board and eye tracker, and the Lighthouse receiver, and only one program
  can hold them. Mirador's headset module stops Pimax's software for the session and checks the rest. Pimax's service, `PiServiceLauncher`, starts
  again every time Windows starts: after a restart, stop it again from an elevated PowerShell with
  `Stop-Service PiServiceLauncher`.
- Administrator rights once, for the install. Running Monado needs none.

## Install

Mirador's installer copies the three files to `{app}\layer` and sets the value `{app}\layer\mirador_direct_mode.json` = 0
(`REG_DWORD`) under `HKLM\SOFTWARE\Khronos\Vulkan\ImplicitLayers`, which is how the Vulkan loader finds an implicit
layer. HKLM is needed because Vulkan ignores per-user layers in elevated programs. An older registration of this layer
under another path or name (the standalone release's `C:\Program Files\DreamAir Vulkan Layer\dreamair_vk_layer.json`,
an older Mirador's, a moved folder) is removed, so
the loader finds one copy only. A profile can name a layer build folder instead; Mirador then adds it to Monado's
`VK_ADD_IMPLICIT_LAYER_PATH`, and that profile's Monado runs as you.

To build it from source: `layer\build.ps1 -Configure -Build -Test` in Mirador's repo (Mirador's `tools\build.ps1` does
it too), into `artifacts\layer-build`.

## Start

Mirador's Start, for a headset that uses NVIDIA direct mode, sets the environment Monado needs, in Monado's process only:

| Variable | Value |
|---|---|
| `MIRADOR_DIRECT_MODE` | `1` |
| `DISABLE_RTSS_LAYER` | `1` |
| `VK_LOADER_LAYERS_ALLOW` | `VK_LAYER_MIRADOR_direct_mode` |
| `XRT_COMPOSITOR_FORCE_VK_DISPLAY` | `0` |
| `XRT_COMPOSITOR_USE_PRESENT_WAIT` | `1` |

and takes out of what Monado inherits variables your user environment may have left set that would change this:
`DISABLE_MIRADOR_DIRECT_MODE`, `IPC_RELAXED_CONNECTION_SECURITY`, `XRT_COMPOSITOR_FORCE_NVAPI_DIRECT`,
`MIRADOR_DIRECT_MODE_TEST_NO_EDID` and `MIRADOR_DIRECT_MODE_IGNORE_HOLDERS`. The headset's module adds its own (for the Dream
Air: `STEAMVR_LH_ENABLE=1`, `U_PACING_COMP_PRESENT_TO_DISPLAY_OFFSET_MS=8.9`, `DREAMAIR_LOG=info`). When the NVIDIA
driver changed since direct mode last passed, Start runs the display check first.

`VK_LOADER_LAYERS_ALLOW` is there because of SteamVR's tracking driver. Monado loads it for the Lighthouse trackers, and
it switches off the Vulkan layers of the process it runs in, this one included; Monado would then find no Vulkan
display. Naming the layer in this variable exempts it. Other layers, such as RTSS's, stay off in Monado, which does no
harm.

The layer writes one line to Monado's log and to `%LOCALAPPDATA%\Mirador\direct-mode\layer.log` for each problem it meets
and says which it is: a Pimax or SteamVR program holding the display, the board asleep, an NVIDIA driver that moved a
call. Such a line starts `[dvl] error:` and the step's name; "Troubleshooting" below says what to do about each. It also
prints a statistics line (presents, flips, missed vblanks) after the first 90 presents of an app session and then every
2700.

If the headset was switched off and on, or unplugged and plugged in again, while Monado ran and it stays black, stop
Monado in Mirador and start it again: the layer sets up the display once per Monado run.

### If you run RivaTuner Statistics Server (RTSS)

`DISABLE_RTSS_LAYER=1` switches off RTSS's Vulkan layer, but RTSS also injects hooks into the process, and those reach
Monado's swapchain anyway. Give `monado-service.exe` an RTSS profile with **application detection set to None** so RTSS
leaves it alone. The check tool warns when RTSS is running.

## Check your driver

`mirador-display-check.exe` tests the NVIDIA side without Monado. It is installed next to the layer. With Pimax,
SteamVR and Monado stopped, in a normal PowerShell:

```powershell
& "$env:ProgramFiles\DreamAir Vulkan Layer\mirador-display-check.exe"
```

It reports the NVIDIA driver version, checks that every NVAPI call the layer needs is there, finds the headset, binds it,
shows colours and counts the display's flips for 20 seconds. It sends the headset board's heartbeat itself (Monado
normally does), so it refuses to run while Pimax or Monado runs and names them. The last line is `PASS: ...` or
`FAIL: ...` with the reason; the exit code is 0 for a pass. A failed step comes with a `what to do:` line (see
"Troubleshooting").

`EDID route: ...` says how the layer recognised the headset: `NVAPI` (the normal case), `registry` (the description
Windows kept from a time the headset was shown as a monitor), or `board` (no description could be read at all; the one
Pimax display is taken to be the Dream Air because the Dream Air's USB board is connected, with a `warning:` line). If
you see `board`, the headset works, but please report it with your driver version.

| Option | |
|---|---|
| (none) | NVAPI only |
| `--vulkan` | The same through the layer, in the order Monado uses it. Needs the layer installed. Sets `MIRADOR_DIRECT_MODE=1` and `DISABLE_RTSS_LAYER=1` in its own process |
| `--seconds=N` | How long to present, 1 to 3600 (default 20) |
| `--no-heartbeat` | Do not send the heartbeat |
| `--help` | The full usage text |

## Troubleshooting

The layer's lines in Monado's console (and the check tool's `FAILED:` lines) start with the step that failed. Find it
in the left column.

| The line starts | What to do |
|---|---|
| `Holders: Pimax or SteamVR is running` | Quit Pimax Play (tray icon > Exit) and SteamVR, stop `PiServiceLauncher` (elevated PowerShell: `Stop-Service PiServiceLauncher`; it starts again with Windows), then start Monado again |
| `LoadNvapi:` | The NVIDIA driver is missing or broken: install NVIDIA's driver. AMD and Intel GPUs are not supported |
| `CheckIds: this driver has no NVAPI call` | Your NVIDIA driver moved a call the layer needs. Run the check tool and report the driver version (below); the tested driver is 621.01 |
| `Enumerate: no direct-mode display of vendor PVR` | The headset did not appear within 10 s. Check that it is connected by DisplayPort and USB and switched on, and that nothing else holds it; unplug and replug it, then start Monado again. The check tool shows whether the board gets its heartbeat |
| `Luid: the display is on adapter` | The headset is connected to another GPU than the one Monado uses: on a laptop, use the port wired to the NVIDIA GPU; with two NVIDIA GPUs, use the one Monado picks |
| `Product: the display's EDID could not be read` | The layer cannot tell which headset this is: neither NVAPI nor Windows gave the headset's description, and the line says why it could not assume the Dream Air (its board, USB 34A4:0044, not in Device Manager; or more than one Pimax display). Connect the Dream Air alone, by DisplayPort and USB, run the check tool and report its output |
| `Product: unknown display product` | The headset is not a Dream Air; this layer drives the Dream Air only |
| `warning: the display's EDID could not be read ...; ... taken to be the Pimax Dream Air` | Not a failure: the headset works. Report it with your driver version, so the layer can learn to read the headset's description on your driver |
| `EnableDirectMode:` or `BindDevice:` | Another program holds the display (Pimax, SteamVR, or a second Monado). Stop it and start Monado again; if it persists, restart Windows (and stop `PiServiceLauncher` again) |
| `D3D11:` | No Direct3D 11 device on the NVIDIA GPU: reinstall or update the NVIDIA driver |
| `CreateBackBuffers:` or `SetDisplayMode:` | The driver refused the Dream Air's mode. Report it with your driver version |
| Monado says "No Vulkan displays found" and the layer printed nothing | The layer was not loaded: is Mirador installed, and does the profile's headset module use direct mode? Run the check tool with `--vulkan` |
| The headset stays black after it was switched off and on or replugged | Quit Monado (key press) and start it again |

Lines start `[dvl] error:` in Monado's console; `MIRADOR_DIRECT_MODE_LOG=warn` keeps the errors and warnings and leaves
out the information lines.

## Never set `MIRADOR_DIRECT_MODE` machine-wide

The layer is switched on by the environment variable `MIRADOR_DIRECT_MODE=1`, and by nothing else. Mirador's Start (for
Monado's process) and the check tool set it for their own process. **Do not set it in your user or system environment.** A machine-wide
setting would load the layer into every Vulkan program you run, games and anti-cheat included.

Without the variable the layer's DLL is never loaded and the program sees nothing extra. The only trace is the layer's
**name**: the Vulkan loader lists every registered implicit layer in every Vulkan program (in `vulkaninfo`, in
`VK_LOADER_DEBUG` output), whatever its enable variable says. Steam's and Epic's overlay layers show up the same way.
That is expected and harmless; the layer is listed there, not loaded.

## Reporting a driver version

If the check tool passes on a driver other than 621.01, or fails on any driver, report it, including:

- the line `NVIDIA driver version: ...` (and the `warning: untested driver version ...` line, if there is one);
- the last line, `PASS: ...` or `FAIL: ...`, and the lines above it for the step that failed. A message like
  `this driver has no NVAPI call '...'` means the driver moved a call; the layer then lists no display instead of guessing;
- your GPU model and Windows version, and whether you ran it with `--vulkan`.

Send it to the maintainer, Finn Zeruhn, where you got this release. The log `%LOCALAPPDATA%\Mirador\direct-mode\layer.log`
helps for problems in Monado.

## Uninstall

Mirador's uninstaller removes the registry value and `{app}\layer`. The layer's per-user log folder
`%LOCALAPPDATA%\Mirador\direct-mode` stays; delete it by hand if you like.

## Legal note

This project exists so that you can use hardware you own with software you choose. The NVIDIA driver calls it uses are
not documented by NVIDIA; their numbers and data layouts were worked out from captures of how Pimax's and SteamVR's own
software uses the driver, for that interoperability.

- It contains **no NVIDIA, Pimax or Valve code, headers or binaries**. It reaches NVAPI only through the
  `nvapi_QueryInterface` function that your installed NVIDIA driver exports, with declarations written for this project.
- It is **not affiliated with, endorsed by or supported by NVIDIA, Pimax or Valve**, or by the Monado project. Those names
  belong to their owners.
- Private driver calls can change with any driver update, which is why the check tool exists. Use it at your own risk.
- This is not legal advice. The software is provided as is, under the Boost Software License 1.0 (see `LICENSE`).
