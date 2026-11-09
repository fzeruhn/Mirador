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
