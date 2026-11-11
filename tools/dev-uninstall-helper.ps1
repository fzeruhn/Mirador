#Requires -Version 7
<#
.SYNOPSIS
    Removes the Mirador helper service that tools\dev-install-helper.ps1 installed. Run it as administrator.
.DESCRIPTION
    Puts back the OpenXR runtime Mirador replaced (the helper's --restore-runtime, which does nothing when Mirador
    replaced none), stops and deletes the MiradorHelper service, and deletes the helper's files from
    %ProgramFiles%\Mirador\: all of them when no Mirador is installed there, else only those the app doesn't use
    (the app's Mirador.deps.json doesn't name them).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    [Console]::Error.WriteLine('The helper service can only be removed from an administrator PowerShell. Open PowerShell as administrator and run tools\dev-uninstall-helper.ps1 again.')
    exit 1
}

$serviceName = 'MiradorHelper'
$appDir = Join-Path $env:ProgramFiles 'Mirador'
$helperExe = Join-Path $appDir 'Mirador.Helper.exe'

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

if (Test-Path -LiteralPath $helperExe) {
    & $helperExe --restore-runtime
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Restoring the OpenXR runtime failed (exit code $LASTEXITCODE); check HKLM\SOFTWARE\Khronos\OpenXR\1 ActiveRuntime."
    }
}

if ($service) {
    sc.exe delete $serviceName
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed (exit code $LASTEXITCODE)."
    }
}

$helperDeps = Join-Path $appDir 'Mirador.Helper.deps.json'
if (Test-Path -LiteralPath (Join-Path $appDir 'Mirador.exe')) {
    # Beside an installed Mirador: the files only the helper's deps.json names, and the helper itself.
    $names = { param($deps) (Get-Content -LiteralPath $deps -Raw | ConvertFrom-Json).targets.PSObject.Properties.Value |
        ForEach-Object { $_.PSObject.Properties.Value } | ForEach-Object { @($_.runtime, $_.native) } |
        Where-Object { $_ } | ForEach-Object { $_.PSObject.Properties.Name } | ForEach-Object { Split-Path $_ -Leaf } }
    if (Test-Path -LiteralPath $helperDeps) {
        $appNames = & $names (Join-Path $appDir 'Mirador.deps.json')
        & $names $helperDeps | Where-Object { $appNames -notcontains $_ } | ForEach-Object {
            Remove-Item -LiteralPath (Join-Path $appDir $_) -Force -ErrorAction SilentlyContinue
        }
    }
    Get-ChildItem -LiteralPath $appDir -Filter 'Mirador.Helper.*' -File | Remove-Item -Force
}
elseif (Test-Path -LiteralPath $appDir) {
    # Only the dev helper was there.
    Remove-Item -LiteralPath $appDir -Recurse -Force
}
$staging = Join-Path $appDir 'helper.new'
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}

Write-Host 'The Mirador helper service is removed.'
