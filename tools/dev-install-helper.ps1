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

# 3. Put the files in place, beside the app's. A file the app has too must be the same bytes (they share one .NET
#    runtime), or nothing is copied: a helper from another commit would change the installed app's files. Never while
#    Mirador runs (its files are locked); robocopy doesn't retry, so a locked file fails the copy at once.
if (Get-Process -Name 'Mirador' -ErrorAction SilentlyContinue) {
    throw 'Mirador is running: quit it (tray icon > Quit), then run this again.'
}
$clash = Get-ChildItem -LiteralPath $stagingDir -Recurse -File | Where-Object {
    $installed = Join-Path $appDir ([IO.Path]::GetRelativePath($stagingDir, $_.FullName))
    $_.Name -notlike 'Mirador.Helper.*' -and (Test-Path -LiteralPath $installed) -and
        (Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $_.FullName).Hash
}
if ($clash) {
    throw "The installed Mirador has other copies of $(($clash | ForEach-Object Name) -join ', '): build the helper from the installed Mirador's commit, or install Mirador from this one. Nothing was copied."
}
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
robocopy $stagingDir $appDir /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
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
