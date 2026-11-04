#Requires -Version 7.3
<#
.SYNOPSIS
    Builds Mirador's installer (artifacts\Mirador-<version>-setup.exe) from the bundle with Inno Setup 6.
.DESCRIPTION
    Run tools\build-bundle.ps1 first. The version is the bundled Mirador.exe's product version. Inno Setup:
    winget install JRSoftware.InnoSetup
#>
[CmdletBinding()]
param(
    [string]$Bundle = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\bundle')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$app = Join-Path $Bundle 'Mirador.exe'
if (-not (Test-Path -LiteralPath $app)) {
    throw "There is no bundle at $Bundle. Run tools\build-bundle.ps1 first."
}
if ((Resolve-Path -LiteralPath $Bundle).Path -ne (Resolve-Path -LiteralPath (Join-Path $repo 'artifacts\bundle')).Path) {
    throw 'The installer takes the bundle from artifacts\bundle (installer\Mirador.iss names it); build it there.'
}

$version = (Get-Item -LiteralPath $app).VersionInfo.ProductVersion
$version = ($version -split '\+')[0]
if (-not $version) {
    throw "$app has no version."
}

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($candidate in (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
                           (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
                           (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')) {
        if (Test-Path -LiteralPath $candidate) {
            $iscc = $candidate
            break
        }
    }
}
if (-not $iscc) {
    throw 'Inno Setup 6 is missing: winget install JRSoftware.InnoSetup'
}

& $iscc "/DAppVersion=$version" (Join-Path $repo 'installer\Mirador.iss')
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed (exit code $LASTEXITCODE)."
}

Write-Host "Installer: $(Join-Path $repo "artifacts\Mirador-$version-setup.exe")"
