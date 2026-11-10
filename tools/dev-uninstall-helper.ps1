#Requires -Version 7
<#
.SYNOPSIS
    Removes the Mirador helper service that tools\dev-install-helper.ps1 installed. Run it as administrator.
.DESCRIPTION
    Puts back the OpenXR runtime Mirador replaced (the helper's --restore-runtime, which does nothing when Mirador
    replaced none), stops and deletes the MiradorHelper service, and deletes the helper's own files from
    %ProgramFiles%\Mirador\ (the .NET runtime's files it shares with the app stay).
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

if (Test-Path -LiteralPath $appDir) {
    Get-ChildItem -LiteralPath $appDir -Filter 'Mirador.Helper.*' -File | Remove-Item -Force
}
$staging = Join-Path $appDir 'helper.new'
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}

Write-Host 'The Mirador helper service is removed.'
