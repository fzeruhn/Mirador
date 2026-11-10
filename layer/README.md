# Mirador's direct-mode layer

`VK_LAYER_MIRADOR_direct_mode`, an implicit Vulkan layer that lets Monado drive a headset NVIDIA's driver keeps in
"direct mode" on Windows: it shows the headset to Monado as an ordinary `VK_KHR_display` display and sends Monado's
frames to it through the driver's private calls, the ones Pimax and SteamVR use. Built for the Pimax Dream Air; other
headsets of a vendor in its table get the mode their EDID asks for, untested.

It ships inside Mirador, in `{app}\layer`: `mirador_direct_mode.dll` and `.json`, and `mirador-display-check.exe`, which
checks the driver and the headset without Monado. Mirador's installer registers the layer and Mirador's Start turns it
on (`MIRADOR_DIRECT_MODE=1`) for Monado alone; its guard declines any other program, and it never belongs in the
machine-wide environment. Its log is in `%LOCALAPPDATA%\Mirador\direct-mode`.

Build and test (Visual Studio's C++ x64 tools, vcpkg for Vulkan-Headers and Catch2):

```powershell
tools\build-cmake.ps1 -Source layer -BuildDir artifacts\layer-build -Configure -Build -Test
```

The design, the tested drivers and how the private calls were worked out are in the project's notes
(`DreamAir_Windows_VkLayer.md`).

## Legal note

This project exists so that you can use hardware you own with software you choose. The NVIDIA driver calls it uses are
not documented by NVIDIA; their numbers and data layouts were worked out from captures of how Pimax's and SteamVR's own
software uses the driver, for that interoperability.

- It contains **no NVIDIA, Pimax or Valve code, headers or binaries**. It reaches NVAPI only through the
  `nvapi_QueryInterface` function that your installed NVIDIA driver exports, with declarations written for this project.
- It is **not affiliated with, endorsed by or supported by NVIDIA, Pimax or Valve**, or by the Monado project. Those names
  belong to their owners.
- Private driver calls can change with any driver update, which is why the check tool exists. Use it at your own risk.
- This is not legal advice. The software is provided as is, under the Boost Software License 1.0 (Mirador's `LICENSE`).
