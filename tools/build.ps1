<#
.SYNOPSIS
    Builds Mirador (Release, x64); with -Test it also runs the tests.
#>
param(
    [switch]$Test
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
        dotnet test Mirador.slnx -c Release -p:Platform=x64 --no-build
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally {
    Pop-Location
}
