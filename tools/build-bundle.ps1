#Requires -Version 7.3
<#
.SYNOPSIS
    Builds Mirador's install bundle (artifacts\bundle) from Finn's builds of Monado, the layer and gaze_world, and the fork's OpenComposite release.
.DESCRIPTION
    The bundle is the install layout of the plan's Global Constraints, which the installer (installer\Mirador.iss)
    copies to {app}:

      Mirador.exe and the app        dotnet publish, self-contained, win-x64
      helper\                        the helper service, self-contained
      monado\runtime\                openxr_monado.json ("Monado (Mirador)", ./openxr_monado.dll) and the DLLs
      monado\runtime\service\        the stand-in, as monado-service.exe: the runtime DLL looks only there
      monado\server\                 the real monado-service.exe and its DLLs
      monado\VERSION.txt             git describe of the Monado source
      standin\monado-service.exe     the stand-in again, copied into build folders by Mirador
      layer\                         dreamair_vk_layer.json, .dll, dreamair-display-check.exe
      tools\                         gaze_world.exe, openxr_loader.dll
      opencomposite\bin\             vrclient_x64.dll and version.txt (empty; Mirador copies them per user)
      opencomposite\                 SOURCE.txt (line 1: version and commit), OpenComposite's licences and notes

    A Monado build that isn't Release or RelWithDebInfo is refused. Debug copies of a DLL (a name ending in "d" next
    to the same name without it, such as SDL2d.dll beside SDL2.dll) are left out.

    OpenComposite is the fork's release (tools\get-opencomposite.ps1 unpacks it to artifacts\opencomposite): bin\vrclient_x64.dll
    and bin\version.txt, and SOURCE.txt and every LICEN[CS]E*/NOTES* file of the archive. It is GPL-3.0-or-later; the
    archive carries its licence and those of what is built into the DLL, and SOURCE.txt says where its source is.
.EXAMPLE
    tools\build-bundle.ps1 -Monado ..\monado-dreamair\build -Layer ..\dreamair-vk-layer\build -GazeWorld ..\DreamAirWin\tools\gaze_world -OpenComposite artifacts\opencomposite
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Monado,
    [Parameter(Mandatory)] [string]$Layer,
    [Parameter(Mandatory)] [string]$GazeWorld,
    [Parameter(Mandatory)] [string]$OpenComposite,
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

# The DLLs of a folder, without debug copies.
function Get-ReleaseDlls([string]$folder) {
    $all = Get-ChildItem -LiteralPath $folder -Filter '*.dll' -File
    $names = $all.Name
    $all | Where-Object {
        -not ($_.BaseName -match '^(.+)d$' -and $names -contains "$($Matches[1]).dll")
    }
}

$Monado = (Resolve-Path -LiteralPath $Monado).Path
$Layer = (Resolve-Path -LiteralPath $Layer).Path
$GazeWorld = (Resolve-Path -LiteralPath $GazeWorld).Path
$OpenComposite = (Resolve-Path -LiteralPath $OpenComposite).Path

# 1. The Monado build: Release or RelWithDebInfo only, and where its source is.
$cache = Join-Path $Monado 'CMakeCache.txt'
Need $cache 'The Monado build folder''s CMakeCache.txt'
$cacheText = Get-Content -LiteralPath $cache
function Get-CacheValue([string]$pattern) {
    $found = $cacheText | Select-String -Pattern $pattern | Select-Object -First 1
    if ($found) { $found.Matches[0].Groups[1].Value } else { '' }
}
$buildType = Get-CacheValue '^CMAKE_BUILD_TYPE:[A-Z]+=(.*)$'
if ($buildType -notin 'Release', 'RelWithDebInfo') {
    throw "The Monado build is '$buildType' (none: a multi-configuration generator such as Visual Studio's). Bundle a Release or RelWithDebInfo build made with Ninja."
}
# Without this cache entry Monado's runtime DLL never starts a service, so games couldn't start Monado through the
# stand-in (nothing in Monado sets it by itself).
if ((Get-CacheValue '^XRT_SERVICE_EXECUTABLE:[A-Z]+=(.*)$') -ne 'monado-service') {
    throw 'The Monado build was configured without -DXRT_SERVICE_EXECUTABLE=monado-service, so games couldn''t start it through Mirador. Configure it again with that option and build it.'
}
$monadoSource = Get-CacheValue '^CMAKE_HOME_DIRECTORY:INTERNAL=(.*)$'
$runtimeBuild = Join-Path $Monado 'src\xrt\targets\openxr'
$serviceBuild = Join-Path $Monado 'src\xrt\targets\service'
Need (Join-Path $runtimeBuild 'openxr_monado.dll') 'Monado''s runtime DLL'
Need (Join-Path $serviceBuild 'monado-service.exe') 'Monado''s service'

# 2. The layer, found as its install.ps1 -Source does.
$layerFiles = @{}
foreach ($dir in $Layer, (Join-Path $Layer 'layer')) {
    if ((Test-Path -LiteralPath (Join-Path $dir 'dreamair_vk_layer.json')) -and (Test-Path -LiteralPath (Join-Path $dir 'dreamair_vk_layer.dll'))) {
        $layerFiles.Manifest = Join-Path $dir 'dreamair_vk_layer.json'
        $layerFiles.Dll = Join-Path $dir 'dreamair_vk_layer.dll'
        break
    }
}
foreach ($dir in $Layer, (Join-Path $Layer 'check')) {
    if (Test-Path -LiteralPath (Join-Path $dir 'dreamair-display-check.exe')) {
        $layerFiles.Check = Join-Path $dir 'dreamair-display-check.exe'
        break
    }
}
if ($layerFiles.Count -ne 3) {
    throw "The layer folder $Layer has no dreamair_vk_layer.json and .dll (in it or its layer folder) and dreamair-display-check.exe (in it or its check folder)."
}
Need (Join-Path $GazeWorld 'gaze_world.exe') 'gaze_world.exe'
Need (Join-Path $GazeWorld 'openxr_loader.dll') 'gaze_world''s openxr_loader.dll'

# OpenComposite: the fork's release, unpacked by tools\get-opencomposite.ps1.
$ocDll = Join-Path $OpenComposite 'bin\vrclient_x64.dll'
Need $ocDll 'OpenComposite''s bin\vrclient_x64.dll (tools\get-opencomposite.ps1)'
Need (Join-Path $OpenComposite 'bin\version.txt') 'OpenComposite''s bin\version.txt (tools\get-opencomposite.ps1)'
$ocSourceTxt = Join-Path $OpenComposite 'SOURCE.txt'
Need $ocSourceTxt 'OpenComposite''s SOURCE.txt (tools\get-opencomposite.ps1)'
$ocFirstLine = (Get-Content -LiteralPath $ocSourceTxt -TotalCount 1).Trim()
if ($ocFirstLine -notmatch '^OpenComposite (\d+(\.\d+)+), commit [0-9a-f]{40}$') {
    throw "The first line of $ocSourceTxt isn't 'OpenComposite <version>, commit <40 hex>': $ocFirstLine"
}
$ocLicences = Get-ChildItem -LiteralPath $OpenComposite -File | Where-Object { $_.Name -match '^(LICEN[CS]E|NOTES)' }
if (-not ($ocLicences | Where-Object Name -eq 'LICENSE.txt')) {
    throw "OpenComposite's LICENSE.txt is missing in $OpenComposite."
}

# 3. A clean bundle folder.
if (Test-Path -LiteralPath $Out) {
    Remove-Item -LiteralPath $Out -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null

# 4. The stand-in.
$standinOut = Join-Path $repo 'artifacts\standin'
& (Join-Path $repo 'standin\build.ps1') -Out $standinOut
$standin = Join-Path $standinOut 'monado-service.exe'
Need $standin 'The stand-in'

# 5. The app (with the stand-in copied in by its project) and the helper.
Invoke-Checked { dotnet publish (Join-Path $repo 'src\Mirador.App\Mirador.App.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $Out } 'Publishing the app'
Invoke-Checked { dotnet publish (Join-Path $repo 'src\Mirador.Helper\Mirador.Helper.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o (Join-Path $Out 'helper') } 'Publishing the helper'
Copy-Item -LiteralPath $standin -Destination (New-Item -ItemType Directory -Force -Path (Join-Path $Out 'standin')).FullName -Force

# 6. Monado: the runtime with the stand-in below it, and the real service where the runtime DLL doesn't look.
$runtimeOut = New-Item -ItemType Directory -Force -Path (Join-Path $Out 'monado\runtime')
$serviceOut = New-Item -ItemType Directory -Force -Path (Join-Path $Out 'monado\runtime\service')
$serverOut = New-Item -ItemType Directory -Force -Path (Join-Path $Out 'monado\server')
Get-ReleaseDlls $runtimeBuild | Copy-Item -Destination $runtimeOut.FullName
Copy-Item -LiteralPath $standin -Destination (Join-Path $serviceOut.FullName 'monado-service.exe')
@{
    file_format_version = '1.0.0'
    runtime             = @{ name = 'Monado (Mirador)'; library_path = './openxr_monado.dll' }
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $runtimeOut.FullName 'openxr_monado.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $serviceBuild 'monado-service.exe') -Destination $serverOut.FullName
Get-ReleaseDlls $serviceBuild | Copy-Item -Destination $serverOut.FullName

# The C++ runtime the MSVC build needs (vcpkg's default triplet links it dynamically), app-local, so Monado runs on a
# PC without the Visual C++ Redistributable. The VS developer environment (entered by standin\build.ps1) says where.
$crt = if ($env:VCToolsRedistDir) { Get-ChildItem -Path (Join-Path $env:VCToolsRedistDir 'x64') -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue | Select-Object -First 1 } else { $null }
if ($crt) {
    foreach ($target in $runtimeOut.FullName, $serverOut.FullName) {
        Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $target -Force
    }
}
else {
    Write-Warning 'The Visual C++ runtime DLLs weren''t found (no VCToolsRedistDir): Monado then needs the Visual C++ Redistributable on the PC.'
}

$version = if ($monadoSource) { git -C $monadoSource describe --always --dirty 2>$null } else { $null }
if (-not $version) {
    $version = "unknown ($Monado)"
}
Set-Content -LiteralPath (Join-Path $Out 'monado\VERSION.txt') -Value $version -Encoding utf8NoBOM

# 7. The layer and gaze_world.
$layerOut = New-Item -ItemType Directory -Force -Path (Join-Path $Out 'layer')
Copy-Item -LiteralPath $layerFiles.Manifest, $layerFiles.Dll, $layerFiles.Check -Destination $layerOut.FullName
$toolsOut = New-Item -ItemType Directory -Force -Path (Join-Path $Out 'tools')
Copy-Item -LiteralPath (Join-Path $GazeWorld 'gaze_world.exe'), (Join-Path $GazeWorld 'openxr_loader.dll') -Destination $toolsOut.FullName

# 8. OpenComposite: the DLL and version.txt (empty: it keeps Steam from rewriting the OpenVR paths file while the
#    copy is first in it), SOURCE.txt and the licences and notes, all from the release.
$ocOut = New-Item -ItemType Directory -Force -Path (Join-Path $Out 'opencomposite')
$ocBin = New-Item -ItemType Directory -Force -Path (Join-Path $ocOut.FullName 'bin')
Copy-Item -LiteralPath $ocDll, (Join-Path $OpenComposite 'bin\version.txt') -Destination $ocBin.FullName
Copy-Item -LiteralPath $ocSourceTxt -Destination $ocOut.FullName
$ocLicences | Copy-Item -Destination $ocOut.FullName

# 9. What was made.
$standinSize = (Get-Item -LiteralPath (Join-Path $serviceOut.FullName 'monado-service.exe')).Length
Write-Host "Bundle: $Out"
Write-Host "  Monado $version ($buildType)"
Write-Host "  stand-in $([math]::Round($standinSize / 1KB)) KB"
Write-Host "  $ocFirstLine"
