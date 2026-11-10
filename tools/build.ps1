<#
.SYNOPSIS
    Builds Mirador (Release, x64); with -Test it also runs the tests.
.PARAMETER SkipHardware
    With -Test: leaves out the tests marked Category=Hardware (they need this PC's headset, devices or set-up), as
    CI does.
#>
param(
    [switch]$Test,
    [switch]$SkipHardware
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

Push-Location $repo
try {
    # The stand-in first: the app's build copies it in, and its tests run it. Without Visual Studio's C++ tools the
    # rest still builds, and those tests are skipped.
    try {
        & (Join-Path $repo 'standin\build.ps1')
    }
    catch {
        Write-Warning "The stand-in wasn't built: $_"
    }

    # The direct-mode layer (layer\, into artifacts\layer-build) and, with -Test, its tests. Without Visual Studio's
    # C++ tools it is skipped as the stand-in is; with them, a failed build or test fails this script. A child
    # process, so its developer shell stays out of this one.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $cppTools = (Test-Path -LiteralPath $vswhere) -and
        [bool](& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($cppTools) {
        $layerArgs = @('-Configure', '-Build') + $(if ($Test) { @('-Test') } else { @() })
        & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $repo 'tools\build-cmake.ps1') -Source (Join-Path $repo 'layer') `
            -BuildDir (Join-Path $repo 'artifacts\layer-build') @layerArgs
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        # xr-check, Verify's OpenXR test client (tools\xr-check): built here, run only by Verify on a PC with a GPU.
        & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $repo 'tools\build-cmake.ps1') -Source (Join-Path $repo 'tools\xr-check') `
            -BuildDir (Join-Path $repo 'artifacts\xr-check-build') -Configure -Build
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    elseif ($env:CI) {
        throw "The layer can't be built on CI: Visual Studio's C++ x64 tools are missing."
    }
    else {
        Write-Warning "The layer wasn't built: Visual Studio's C++ x64 tools are missing."
    }

    dotnet build Mirador.slnx -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($Test) {
        $filter = if ($SkipHardware) { @('--filter', 'Category!=Hardware') } else { @() }
        dotnet test Mirador.slnx -c Release -p:Platform=x64 --no-build --logger trx @filter
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally {
    Pop-Location
}
