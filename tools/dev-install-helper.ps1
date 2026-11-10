#Requires -Version 7.3
<#
.SYNOPSIS
    Installs the Mirador helper service from this checkout, for development. Run it as administrator.
.DESCRIPTION
    Publishes the helper (self-contained) into %ProgramFiles%\Mirador\, a folder only administrators can write to
    (never into the repository: the service runs as LocalSystem), beside the app as the installer puts it, then
    creates the MiradorHelper service with sc.exe, or, when it exists already, stops it, replaces the files and starts
    it again. Over an installed Mirador, build from the same commit: they share the .NET runtime's files.

    Check it with:  sc.exe query MiradorHelper
    Take it out with:  tools\dev-uninstall-helper.ps1

    The installer that ships with Mirador puts the same files in the same place.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    [Console]::Error.WriteLine('The helper service can only be installed from an administrator PowerShell. Open PowerShell as administrator and run tools\dev-install-helper.ps1 again.')
    exit 1
}

$repo = Split-Path -Parent $PSScriptRoot
$serviceName = 'MiradorHelper'
$appDir = Join-Path $env:ProgramFiles 'Mirador'
$stagingDir = Join-Path $appDir 'helper.new'
$helperExe = Join-Path $appDir 'Mirador.Helper.exe'

# 1. Publish next to the final folder (also administrators-only), so a failed build leaves a working helper alone.
if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}
dotnet publish (Join-Path $repo 'src\Mirador.Helper\Mirador.Helper.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $stagingDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit code $LASTEXITCODE). The installed helper, if any, was not touched."
}

# 2. Stop the running service so its files can be replaced.
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

# 3. Put the files in place, beside the app's (copied over them, never mirrored: the app's own files stay).
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
robocopy $stagingDir $appDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) {
    throw "Copying the helper into $appDir failed (robocopy exit code $LASTEXITCODE)."
}
Remove-Item -LiteralPath $stagingDir -Recurse -Force

# 4. Create the service, or start the one that was there. The quotes around the path stay in the service's
#    command line (a path with spaces must be quoted there); PowerShell 7 passes them on as \".
if (-not $service) {
    sc.exe create $serviceName binPath= "`"$helperExe`"" start= auto DisplayName= 'Mirador helper'
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe create failed (exit code $LASTEXITCODE)."
    }
}
Start-Service -Name $serviceName

sc.exe query $serviceName
Write-Host "The Mirador helper service is installed from $appDir."
