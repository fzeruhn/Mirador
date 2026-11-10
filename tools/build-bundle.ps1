#Requires -Version 7.3
<#
.SYNOPSIS
    Builds Mirador's install bundle (artifacts\bundle): the app, the helper, the stand-in, the direct-mode layer and the
    tools a branch build needs. Monado and OpenComposite aren't in it: the helper downloads the bundled Monado, and
    "Get OpenComposite" the fork's release (Mirador.md §4.11 items 55 and 56).
.DESCRIPTION
    The bundle is the install layout of the plan's Global Constraints, which the installer (installer\Mirador.iss)
    copies to {app}:

      Mirador.exe and the app        dotnet publish, self-contained, win-x64
      Mirador.Helper.exe             the helper service, beside the app on the same .NET runtime
      standin\monado-service.exe     the stand-in again, copied into build folders by Mirador
      layer\                         mirador_direct_mode.json, .dll, mirador-display-check.exe (built from layer\)
      tools\                         build-monado.ps1, vcpkg-commit.txt, xr-check.exe
.EXAMPLE
    tools\build-bundle.ps1
#>
[CmdletBinding()]
param(
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\bundle')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Need([string]$path, [string]$what) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "$what is missing: $path"
    }
}

function Invoke-Checked([scriptblock]$command, [string]$what) {
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed (exit code $LASTEXITCODE)."
    }
}

# 1. A clean bundle folder.
if (Test-Path -LiteralPath $Out) {
    Remove-Item -LiteralPath $Out -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null

# 2. The stand-in.
$standinOut = Join-Path $repo 'artifacts\standin'
& (Join-Path $repo 'standin\build.ps1') -Out $standinOut
$standin = Join-Path $standinOut 'monado-service.exe'
Need $standin 'The stand-in'

# The direct-mode layer, from layer\ (a child process, so its developer shell stays out of this one).
$layerBuild = Join-Path $repo 'artifacts\layer-build'
Invoke-Checked { & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $repo 'tools\build-cmake.ps1') -Source (Join-Path $repo 'layer') -Configure -Build -BuildDir $layerBuild } 'Building the layer'
$layerFiles = (Join-Path $layerBuild 'layer\mirador_direct_mode.json'), (Join-Path $layerBuild 'layer\mirador_direct_mode.dll'),
    (Join-Path $layerBuild 'check\mirador-display-check.exe')
foreach ($file in $layerFiles) {
    Need $file 'The layer''s build'
}

# xr-check, Verify's OpenXR test client (Mirador.md §4.11 item 49), from tools\xr-check.
$xrCheckBuild = Join-Path $repo 'artifacts\xr-check-build'
Invoke-Checked { & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $repo 'tools\build-cmake.ps1') -Source (Join-Path $repo 'tools\xr-check') -BuildDir $xrCheckBuild -Configure -Build } 'Building xr-check'
$xrCheck = Join-Path $xrCheckBuild 'xr-check.exe'
Need $xrCheck 'xr-check''s build'

# 3. The app (with the stand-in copied in by its project) and the helper.
Invoke-Checked { dotnet publish (Join-Path $repo 'src\Mirador.App\Mirador.App.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $Out } 'Publishing the app'
# The helper shares the app's folder and .NET runtime: published apart, it may only add files or bring the same bytes,
# so neither program ever runs on a DLL built for the other.
$helperOut = "$Out.helper"
if (Test-Path -LiteralPath $helperOut) { Remove-Item -LiteralPath $helperOut -Recurse -Force }
Invoke-Checked { dotnet publish (Join-Path $repo 'src\Mirador.Helper\Mirador.Helper.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $helperOut } 'Publishing the helper'
$clash = Get-ChildItem -LiteralPath $helperOut -Recurse -File | Where-Object {
    $app = Join-Path $Out ([IO.Path]::GetRelativePath($helperOut, $_.FullName))
    (Test-Path -LiteralPath $app) -and (Get-FileHash -LiteralPath $app).Hash -ne (Get-FileHash -LiteralPath $_.FullName).Hash
}
if ($clash) { throw "The helper and the app ship different copies of: $(($clash | ForEach-Object Name) -join ', ')." }
Copy-Item -Path (Join-Path $helperOut '*') -Destination $Out -Recurse -Force
Remove-Item -LiteralPath $helperOut -Recurse -Force
Copy-Item -LiteralPath $standin -Destination (New-Item -ItemType Directory -Force -Path (Join-Path $Out 'standin')).FullName -Force
# The bundled Monado's build recipe and its vcpkg pin, which the app runs to build a profile's branch (Mirador.md §4.11
# item 49).
$toolsDir = New-Item -ItemType Directory -Force -Path (Join-Path $Out 'tools')
Copy-Item -LiteralPath (Join-Path $repo 'tools\build-monado.ps1'), (Join-Path $repo 'tools\vcpkg-commit.txt'), $xrCheck -Destination $toolsDir.FullName

# 4. The layer.
$layerOut = New-Item -ItemType Directory -Force -Path (Join-Path $Out 'layer')
Copy-Item -LiteralPath $layerFiles -Destination $layerOut.FullName

# 5. What was made.
$standinSize = (Get-Item -LiteralPath $standin).Length
Write-Host "Bundle: $Out"
Write-Host "  stand-in $([math]::Round($standinSize / 1KB)) KB"
Write-Host '  the layer'
