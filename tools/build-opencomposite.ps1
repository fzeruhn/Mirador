#Requires -Version 7.3
<#
.SYNOPSIS
    Builds OpenComposite's 64-bit vrclient_x64.dll from its pinned commit on Mirador's fork, for the bundle
    (tools\build-bundle.ps1 -OpenComposite / -OpenCompositeSource).
.DESCRIPTION
    OpenComposite turns OpenVR into OpenXR, so OpenVR (SteamVR) games run on Monado (Mirador.md section 4.6). It is
    GPL-3.0-or-later and ships as its own DLL, which Mirador never links or loads: games do. Upstream
    (gitlab.com/znixian/OpenOVR) has merged nothing since 2025-07, so Mirador builds its fork,
    github.com/fzeruhn/OpenComposite, whose main line (openxr) carries the upstream merge requests games need.

    The source is cloned into -Source when it isn't there (an older clone's origin is pointed at the fork), then
    checked out at the pinned commit with its submodules. A tracked change anywhere in it is refused: the bundle carries
    the fork's commit as it is. OpenComposite wants the Vulkan headers and vulkan-1.lib in libs\vulkan (an untracked
    folder); they are copied from -Vulkan, which can be a Vulkan SDK or vcpkg's x64-windows folder (both have
    include\vulkan and lib\vulkan-1.lib).

    The build is Ninja, Release, with the C runtime linked in (/MT): the DLL is loaded into games, next to which no
    Visual C++ runtime may be, and Windows doesn't look in the DLL's own folder for its dependencies.
.EXAMPLE
    tools\build-opencomposite.ps1 -Vulkan ..\monado-dreamair\build\vcpkg_installed\x64-windows
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Vulkan,
    [string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\opencomposite-src'),
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\opencomposite-build')
)

$ErrorActionPreference = 'Stop'

# The pinned commit (the fork's openxr, 2026-11-06). A new one is a deliberate change: build, test with Monado, then bump.
$Repository = 'https://github.com/fzeruhn/OpenComposite.git'
$Commit = 'd6fc6841ae815457c3efb7b725ad75e4ba541ffd'

function Invoke-Checked([scriptblock]$command, [string]$what) {
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed (exit code $LASTEXITCODE)."
    }
}

$Vulkan = (Resolve-Path -LiteralPath $Vulkan).Path
$vulkanHeaders = Join-Path $Vulkan 'include\vulkan'
$vulkanLib = Join-Path $Vulkan 'lib\vulkan-1.lib'
foreach ($path in $vulkanHeaders, $vulkanLib) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "-Vulkan $Vulkan has no $path. Give a Vulkan SDK folder, or vcpkg's x64-windows folder of a Monado build."
    }
}

# 1. The source at the pinned commit, with its submodules, and nothing changed in it.
if (-not (Test-Path -LiteralPath (Join-Path $Source '.git'))) {
    Invoke-Checked { git clone --no-checkout $Repository $Source } 'Cloning OpenComposite'
}

Invoke-Checked { git -C $Source remote set-url origin $Repository } 'Pointing the clone at the fork'
Invoke-Checked { git -C $Source fetch --quiet origin } 'Fetching OpenComposite'
Invoke-Checked { git -C $Source -c advice.detachedHead=false checkout --quiet $Commit } "Checking out $Commit"
Invoke-Checked { git -C $Source submodule update --init --recursive --quiet } 'Fetching the submodules'
git -C $Source diff --quiet HEAD
$changed = $LASTEXITCODE -ne 0
git -C $Source submodule foreach --quiet --recursive 'git diff --quiet HEAD'
if ($changed -or $LASTEXITCODE -ne 0) {
    throw "The OpenComposite source in $Source has local changes. The bundle carries the pinned commit as it is: undo them (git checkout -- . and the same in the submodules)."
}

# 2. Vulkan, where OpenComposite's CMakeLists.txt looks on Windows.
$vulkanOut = Join-Path $Source 'libs\vulkan'
New-Item -ItemType Directory -Force -Path (Join-Path $vulkanOut 'Include'), (Join-Path $vulkanOut 'Lib') | Out-Null
Copy-Item -LiteralPath $vulkanHeaders -Destination (Join-Path $vulkanOut 'Include') -Recurse -Force
$vkVideo = Join-Path $Vulkan 'include\vk_video'
if (Test-Path -LiteralPath $vkVideo) {
    Copy-Item -LiteralPath $vkVideo -Destination (Join-Path $vulkanOut 'Include') -Recurse -Force
}
Copy-Item -LiteralPath $vulkanLib -Destination (Join-Path $vulkanOut 'Lib') -Force

# 3. The Visual Studio developer shell (the C++ x64 tools, CMake and Ninja come with it). OpenComposite includes ATL's
#    atlbase.h, which only an install with the ATL component has (not every Build Tools one).
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = if (Test-Path $vswhere) {
        & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.VC.ATL -property installationPath
    }
    else { $null }
    if (-not $vs) {
        throw 'OpenComposite needs Visual Studio (or its Build Tools) with the C++ x64 tools and "C++ ATL" (Microsoft.VisualStudio.Component.VC.ATL).'
    }

    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
}

# 4. Configure and build only the DLL (the RuntimeSwitcher C# project is OpenComposite's own launcher: not wanted).
#    The version starts with the commit: tools\build-bundle.ps1 checks the build against the source folder's HEAD.
#    The C++ flags are CMake's defaults for MSVC, given so that an older build folder's cache (which had /FIchrono
#    before the fork included <chrono> itself) doesn't keep its own.
$version = "$Commit (Mirador)"
Invoke-Checked {
    cmake -S $Source -B $Out -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded `
        '-DCMAKE_CXX_FLAGS=/DWIN32 /D_WINDOWS /GR /EHsc' -DERROR_ON_WARNING=ON "-DOC_VERSION=$version"
} 'Configuring OpenComposite'
Invoke-Checked { cmake --build $Out --target OCOVR } 'Building OpenComposite'

$dll = Join-Path $Out 'bin\vrclient_x64.dll'
if (-not (Test-Path -LiteralPath $dll)) {
    throw "The build made no $dll."
}

Write-Host "OpenComposite $Commit"
Write-Host "  $dll ($([math]::Round((Get-Item -LiteralPath $dll).Length / 1KB)) KB)"
