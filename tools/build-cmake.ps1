# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0
#
# Configure, build and test one of Mirador's C++ projects (the direct-mode layer, xr-check) in a Visual Studio
# developer shell, with vcpkg's x64-windows-static triplet (the static CRT: nothing needs the Visual C++
# Redistributable), Ninja and RelWithDebInfo.
#   pwsh -NoProfile -File tools\build-cmake.ps1 -Source layer -BuildDir artifacts\layer-build -Configure -Build -Test
# Visual Studio (or its Build Tools) with the C++ x64 tools is found with vswhere, as for the stand-in and Monado's
# recipe; -VsPath names one instead. vcpkg: VCPKG_ROOT, else Mirador's pinned copy (%LOCALAPPDATA%\Mirador\tools\vcpkg),
# else the one inside that Visual Studio (it fetches vcpkg.json's baseline itself), else VCPKG_INSTALLATION_ROOT
# (GitHub's runners; its clone must hold the baseline commit).
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$BuildDir,
    [switch]$Configure,
    [switch]$Build,
    [switch]$Test,
    [string]$VsPath,
    [string[]]$Extra = @()
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Source).Path
$buildDir = [IO.Path]::GetFullPath($BuildDir)
$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
$env:PATH = "$installer;$env:PATH"
if ($VsPath) {
    $vs = $VsPath
} else {
    $vswhere = Join-Path $installer 'vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "vswhere.exe is not in ${installer}: install Visual Studio or its Build Tools, or give -VsPath"
    }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) {
        throw ('no Visual Studio with the C++ x64 tools found (vswhere); install the "Desktop development with C++" ' +
            'workload or the Build Tools, or give -VsPath')
    }
}
$vcpkg = @(
    $env:VCPKG_ROOT,
    $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Mirador\tools\vcpkg' }),
    (Join-Path $vs 'VC\vcpkg'),
    $env:VCPKG_INSTALLATION_ROOT
) | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'scripts\buildsystems\vcpkg.cmake') -PathType Leaf) } |
    Select-Object -First 1
if (-not $vcpkg) {
    throw "no vcpkg found: set VCPKG_ROOT, or add the 'vcpkg package manager' component to $vs in the Visual Studio Installer"
}
$toolchain = Join-Path $vcpkg 'scripts\buildsystems\vcpkg.cmake'
$env:VCPKG_DISABLE_METRICS = '1'
# vcpkg picks the newest Visual Studio on its own. Its static libraries then may need a newer STL than the toolset
# that links the tests, so pin vcpkg to the same Visual Studio.
$env:VCPKG_VISUAL_STUDIO_PATH = $vs
Import-Module (Join-Path $vs "Common7\Tools\Microsoft.VisualStudio.DevShell.dll")
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -no_logo -host_arch=amd64' | Out-Null
if ($Configure) {
    $cmakeArgs = @(
        "-S", $source, "-B", $buildDir, "-GNinja",
        "-DCMAKE_BUILD_TYPE=RelWithDebInfo",
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
        "-DVCPKG_TARGET_TRIPLET=x64-windows-static"
    ) + $Extra
    cmake @cmakeArgs
    if ($LASTEXITCODE) { throw "configure failed ($LASTEXITCODE)" }
}
if ($Build) {
    cmake --build $buildDir
    if ($LASTEXITCODE) { throw "build failed ($LASTEXITCODE)" }
}
if ($Test) {
    ctest --test-dir $buildDir --output-on-failure
    if ($LASTEXITCODE) { throw "tests failed ($LASTEXITCODE)" }
}
