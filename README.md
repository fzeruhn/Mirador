# Mirador

A Windows app for running [Monado](https://monado.freedesktop.org/), the open-source OpenXR runtime. Mirador installs
Monado, starts and stops it, switches the active OpenXR runtime, starts Monado when a game needs it, and keeps
headset vendor software out of the way while it runs. Its first supported headset is the Pimax Dream Air.

Work in progress: nothing is released yet.

## Building

You need Windows 10 version 2004 or newer, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and the
Windows 11 SDK (10.0.26100). The app is WinUI 3, unpackaged and self-contained, for x64 only.

```powershell
tools\build.ps1          # Release build of Mirador.slnx
tools\build.ps1 -Test    # the same, then all tests
dotnet run --project src\Mirador.App -p:Platform=x64
```

The stand-in (`standin\`, C) needs Visual Studio's C++ x64 tools; `tools\build.ps1` builds it first (with a warning,
not a failure, when they're missing) into `artifacts\standin\monado-service.exe`, and `standin\build.ps1` builds it
alone.

OpenComposite, which runs OpenVR games on Monado: `tools\build-opencomposite.ps1 -Vulkan <dir>` clones it into
`artifacts\opencomposite-src` at its pinned commit and builds `artifacts\opencomposite-build\bin\vrclient_x64.dll`.
`-Vulkan` is a folder with `include\vulkan` and `lib\vulkan-1.lib` (a Vulkan SDK, or a Monado build's
`vcpkg_installed\x64-windows`). It needs Visual Studio's C++ x64 tools with "C++ ATL".

The installer: `tools\build-bundle.ps1 -Monado <build> -Layer <layer build> -GazeWorld <dir> -OpenComposite
artifacts\opencomposite-build -OpenCompositeSource artifacts\opencomposite-src` puts the install layout in
`artifacts\bundle`, then `tools\build-installer.ps1` (Inno Setup 6: `winget install JRSoftware.InnoSetup`) makes
`artifacts\Mirador-<version>-setup.exe` from it.

## Layout

| Project | What it is |
|---|---|
| `src\Mirador.App` | The WinUI 3 app (`Mirador.exe`) |
| `src\Mirador.Core` | UI-free core: data paths, profiles, the Monado service and the session |
| `src\Mirador.DreamAir` | The Pimax Dream Air module |
| `src\Mirador.Ipc` | Messages between the app and the helper service |
| `src\Mirador.Helper` | The helper Windows service, which does the steps that need admin rights |
| `standin\` | The stand-in (C): sits where Monado's runtime DLL looks for `monado-service.exe` and asks Mirador to start Monado |
| `installer\` | The Inno Setup script |
| `tests\` | xUnit tests, one project per source project |

Mirador keeps its data in `%LOCALAPPDATA%\Mirador\`.

The helper service is not part of a plain build. To try it on a development machine, run
`tools\dev-install-helper.ps1` from an administrator PowerShell (it installs the service `MiradorHelper` from
`%ProgramFiles%\Mirador\helper\`) and `tools\dev-uninstall-helper.ps1` to take it out again.

## Dependencies

Every version is pinned in `Directory.Packages.props` (central package management); stable releases only.

- .NET 10, `net10.0-windows10.0.26100.0`, minimum Windows version 10.0.19041.0.
- Windows App SDK 2.6.5 (WinUI 3), self-contained.
- The helper service uses `Microsoft.Extensions.Hosting` 10.0.14, `Microsoft.Extensions.Hosting.WindowsServices`
  10.0.14 and `System.Management` 10.0.14 (the newest stable releases).
- xUnit 2.9.3 with `xunit.runner.visualstudio` 4.0.1 and `Microsoft.NET.Test.Sdk` 18.11.0, on VSTest.
- `CommunityToolkit.WinUI.Controls.SettingsControls` 8.2.251219 and `H.NotifyIcon.WinUI` 2.4.1: both declare only
  "Windows App SDK 1.6 or newer", so they were checked against 2.6.5. Both restored, compiled and ran there (a
  `SettingsCard` holding a working `Slider`, and a tray icon with a one-item menu that Windows registered), so Mirador
  uses them as they are, with no `SettingsRow` of its own and no `Shell_NotifyIconW` code.
- `CommunityToolkit.Mvvm` 8.4.2 for the pages' view models.
- The icons in `src\Mirador.App\Assets` are drawn by `tools\make-icons.py` (Python with Pillow).

## License

[Boost Software License 1.0](LICENSE).
