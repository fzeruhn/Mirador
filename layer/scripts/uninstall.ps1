# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

<#
.SYNOPSIS
Removes the Dream Air Vulkan layer: unregisters it from the Vulkan loader and deletes its files from Program Files.

.DESCRIPTION
Run it from an elevated PowerShell (run as administrator). It reverses install.ps1:

  1. removes the value  <install folder>\dreamair_vk_layer.json  under HKLM\SOFTWARE\Khronos\Vulkan\ImplicitLayers,
     and any older registration of this layer under another path (a value that ends in dreamair_vk_layer.json);
  2. deletes dreamair_vk_layer.dll, dreamair_vk_layer.json and dreamair-display-check.exe from
     %ProgramFiles%\DreamAir Vulkan Layer\ and the folder itself if it is then empty.

It leaves the layer's per-user log (%LOCALAPPDATA%\DreamAirVkLayer) alone. Quit Monado first: a running layer DLL
cannot be deleted. Running it again when nothing is installed is harmless.

.PARAMETER DryRun
Print the files and the registry values that would be removed, and remove nothing. Also -WhatIf. Needs no elevation.

.EXAMPLE
.\uninstall.ps1 -DryRun

.EXAMPLE
.\uninstall.ps1
#>
[CmdletBinding()]
param(
    [Alias('WhatIf')]
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'

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
    Write-Host 'dry run: nothing is removed'
    if (-not (Test-Admin)) {
        Write-Host 'note: this PowerShell is not elevated; a real run would refuse (run as administrator)'
    }
} elseif (-not (Test-Admin)) {
    Fail ('uninstall.ps1 changes Program Files and HKLM, so it needs an elevated PowerShell. Right-click Windows ' +
        'PowerShell (or Terminal) > Run as administrator, then run it again.')
}

# --- What is registered: the current path, and this layer under any other path.
function Open-ImplicitLayersKey([bool]$Writable) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    try {
        return $base.OpenSubKey($registrySubKey, $Writable)
    } finally {
        $base.Dispose()
    }
}

$registered = @()
$key = Open-ImplicitLayersKey $false
if ($key) {
    try {
        $registered = @($key.GetValueNames() | Where-Object { $_ -match '(^|[\\/])dreamair_vk_layer\.json$' })
    } finally {
        $key.Dispose()
    }
}

$files = @($dllName, $manifestName, $checkName) | ForEach-Object { Join-Path $installDir $_ }

Write-Host "registry: $registryPath"
if ($registered.Count -eq 0) {
    Write-Host '  no registration of the layer found'
}
foreach ($name in $registered) {
    Write-Host ("  remove value '{0}'{1}" -f $name, $(if ($name -ine $manifestPath) { ' (an older path)' } else { '' }))
}
Write-Host "install folder: $installDir"
foreach ($f in $files) {
    $note = if (Test-Path -LiteralPath $f -PathType Leaf) { '' } else { ' (not there)' }
    Write-Host ('  delete {0}{1}' -f $f, $note)
}
Write-Host '  delete the folder itself if it is then empty'

if ($DryRun) {
    Write-Host 'dry run: done, nothing was touched'
    exit 0
}

# The registration first: from then on no new process loads the layer, even if a file below cannot be deleted.
if ($registered.Count -gt 0) {
    $key = Open-ImplicitLayersKey $true
    try {
        foreach ($name in $registered) {
            $key.DeleteValue($name, $false)
        }
    } finally {
        $key.Dispose()
    }
}

$stuck = @()
foreach ($f in $files) {
    if (Test-Path -LiteralPath $f -PathType Leaf) {
        try {
            Remove-Item -LiteralPath $f -Force
        } catch {
            $stuck += $f
            Write-Host "error: cannot delete ${f}: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}
if ($stuck.Count -gt 0) {
    Fail ('the layer is unregistered, but some files are still there (in use?). Quit Monado and the check tool and ' +
        'run this script again.')
}
if ((Test-Path -LiteralPath $installDir -PathType Container) -and
    -not (Get-ChildItem -LiteralPath $installDir -Force | Select-Object -First 1)) {
    Remove-Item -LiteralPath $installDir -Force
} elseif (Test-Path -LiteralPath $installDir -PathType Container) {
    Write-Host "note: $installDir holds other files, so the folder is left"
}
Write-Host 'uninstalled'
