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
