# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

<#
.SYNOPSIS
Installs the Dream Air Vulkan layer: copies it to Program Files and registers it with the Vulkan loader.

.DESCRIPTION
Run it from an elevated PowerShell (run as administrator). It does two things and nothing else:

  1. copies dreamair_vk_layer.dll, dreamair_vk_layer.json and dreamair-display-check.exe to
     %ProgramFiles%\DreamAir Vulkan Layer\
  2. sets the value  <that folder>\dreamair_vk_layer.json = 0  (REG_DWORD) under
     HKLM\SOFTWARE\Khronos\Vulkan\ImplicitLayers, which is how the Vulkan loader finds an implicit layer. An older
     registration of this layer under another path is removed.

The layer then appears by name in every Vulkan program's layer list, but it is only loaded in a process that has
DREAMAIR_VK_LAYER=1 in its environment (start-monado.ps1 and the check tool set it; set it nowhere else).

The files are taken from the folder this script is in (a release), else from ..\build\layer and ..\build\check (a
build of the repository), or from -Source.

.PARAMETER Source
A folder holding dreamair_vk_layer.dll, dreamair_vk_layer.json and dreamair-display-check.exe, or a build folder with
layer\ and check\ below it. Default: this script's folder, else the repository's build folder.

.PARAMETER DryRun
Print the files and the registry value that would be touched, and touch nothing. Also -WhatIf. Needs no elevation.

.EXAMPLE
.\install.ps1 -DryRun

.EXAMPLE
.\install.ps1
#>
[CmdletBinding()]
param(
    [string]$Source,
    [Alias('WhatIf')]
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'

$layerName = 'VK_LAYER_DREAMAIR_nvapi_direct'
$dllName = 'dreamair_vk_layer.dll'
$manifestName = 'dreamair_vk_layer.json'
$checkName = 'dreamair-display-check.exe'
$registryPath = 'HKLM\SOFTWARE\Khronos\Vulkan\ImplicitLayers'
$registrySubKey = 'SOFTWARE\Khronos\Vulkan\ImplicitLayers'

# The 64-bit Program Files, also from a 32-bit PowerShell.
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$installDir = Join-Path $programFiles 'DreamAir Vulkan Layer'
$manifestPath = Join-Path $installDir $manifestName

function Fail([string]$Message) {
    Write-Host "error: $Message" -ForegroundColor Red
    exit 1
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($DryRun) {
    Write-Host 'dry run: nothing is copied, registered or removed'
    if (-not (Test-Admin)) {
        Write-Host 'note: this PowerShell is not elevated; a real run would refuse (run as administrator)'
    }
} elseif (-not (Test-Admin)) {
    Fail ('install.ps1 writes to Program Files and HKLM, so it needs an elevated PowerShell. Right-click Windows ' +
        'PowerShell (or Terminal) > Run as administrator, then run it again.')
}

# --- Find the files: the DLL and its manifest from one folder, the check tool from the first folder that has it.
if ($Source) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { Fail "-Source is not a folder: $Source" }
    $layerDirs = @($Source, (Join-Path $Source 'layer'))
    $checkDirs = @($Source, (Join-Path $Source 'check'))
} else {
    $layerDirs = @($PSScriptRoot, (Join-Path $PSScriptRoot '..\build\layer'))
    $checkDirs = @($PSScriptRoot, (Join-Path $PSScriptRoot '..\build\check'))
}
$layerDir = $layerDirs | Where-Object {
    (Test-Path -LiteralPath (Join-Path $_ $dllName) -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $_ $manifestName) -PathType Leaf)
} | Select-Object -First 1
if (-not $layerDir) {
    Fail ("cannot find $dllName together with $manifestName in: " + ($layerDirs -join '; ') +
        '. Build the layer (build.ps1 -Build) or run this script from the unpacked release, or give -Source.')
}
$checkDir = $checkDirs | Where-Object { Test-Path -LiteralPath (Join-Path $_ $checkName) -PathType Leaf } |
    Select-Object -First 1
if (-not $checkDir) {
    Fail ("cannot find $checkName in: " + ($checkDirs -join '; ') + '. Build it (build.ps1 -Build) or give -Source.')
}
$layerDir = (Resolve-Path -LiteralPath $layerDir).Path
$checkDir = (Resolve-Path -LiteralPath $checkDir).Path

# The manifest must be ours, and must load the DLL we are about to copy next to it.
try {
    $layer = (Get-Content -LiteralPath (Join-Path $layerDir $manifestName) -Raw | ConvertFrom-Json).layer
} catch {
    Fail "cannot read $(Join-Path $layerDir $manifestName): $($_.Exception.Message)"
}
if ($layer.name -ne $layerName) {
    Fail "$(Join-Path $layerDir $manifestName) is not the manifest of $layerName (it names '$($layer.name)')"
}
if ((Split-Path -Leaf $layer.library_path) -ne $dllName) {
    Fail "the manifest loads '$($layer.library_path)', not $dllName"
}

$copies = @(
    [pscustomobject]@{ From = (Join-Path $layerDir $dllName);      To = (Join-Path $installDir $dllName) },
    [pscustomobject]@{ From = (Join-Path $layerDir $manifestName); To = $manifestPath },
    [pscustomobject]@{ From = (Join-Path $checkDir $checkName);    To = (Join-Path $installDir $checkName) }
)

# --- Registrations of this layer under another path (an older install, a moved folder).
function Open-ImplicitLayersKey([bool]$Writable) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    try {
        if ($Writable) { return $base.CreateSubKey($registrySubKey) }
        return $base.OpenSubKey($registrySubKey)
    } finally {
        $base.Dispose()
    }
}

$stale = @()
$key = Open-ImplicitLayersKey $false
if ($key) {
    try {
        $stale = @($key.GetValueNames() | Where-Object {
            $_ -match '(^|[\\/])dreamair_vk_layer\.json$' -and $_ -ine $manifestPath
        })
    } finally {
        $key.Dispose()
    }
}

# --- Do it (or say it).
Write-Host "install folder: $installDir"
foreach ($c in $copies) {
    Write-Host ('  copy {0}' -f $c.From)
    Write-Host ('    to {0}' -f $c.To)
}
Write-Host "registry: $registryPath"
Write-Host "  set value '$manifestPath' = 0 (REG_DWORD)"
foreach ($old in $stale) {
    Write-Host "  remove the older registration '$old'"
}

if ($DryRun) {
    Write-Host 'dry run: done, nothing was touched'
    exit 0
}

try {
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    foreach ($c in $copies) {
        Copy-Item -LiteralPath $c.From -Destination $c.To -Force
    }
} catch {
    Fail ("cannot copy into ${installDir}: $($_.Exception.Message) If Monado or the check tool is running, quit it " +
        'and run this script again. The registry was not changed.')
}

$key = Open-ImplicitLayersKey $true
try {
    foreach ($old in $stale) {
        $key.DeleteValue($old, $false)
    }
    $key.SetValue($manifestPath, 0, [Microsoft.Win32.RegistryValueKind]::DWord)
} finally {
    $key.Dispose()
}

Write-Host ''
Write-Host 'installed. Next, from a normal (not elevated) PowerShell with Pimax stopped:'
Write-Host "  & '$(Join-Path $installDir $checkName)'                 (checks the driver and the display)"
Write-Host '  .\start-monado.ps1                                       (starts Monado through the layer)'
