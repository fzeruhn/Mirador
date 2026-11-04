<#
.SYNOPSIS
    Builds Mirador's stand-in (artifacts\standin\monado-service.exe) with MSVC, from a Visual Studio developer
    shell or, when cl.exe isn't on the path, by entering one found with vswhere (the C++ x64 tools are needed).
#>
param(
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\standin')
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'The stand-in needs Visual Studio (or its Build Tools) with the C++ x64 tools: vswhere.exe is missing.'
    }

    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) {
        throw 'The stand-in needs Visual Studio (or its Build Tools) with the C++ x64 tools ("Desktop development with C++").'
    }

    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
}

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$obj = Join-Path $Out 'obj'
New-Item -ItemType Directory -Force -Path $obj | Out-Null

Push-Location $PSScriptRoot
try {
    rc.exe /nologo /fo (Join-Path $obj 'standin.res') standin.rc
    if ($LASTEXITCODE -ne 0) { throw "rc.exe failed (exit code $LASTEXITCODE)." }

    # A windows-subsystem program: a game without a console starts it without a console window popping up. Its
    # messages still reach a caller that redirects stderr.
    cl.exe /nologo /O2 /MT /W4 /WX /DUNICODE /D_UNICODE standin.c (Join-Path $obj 'standin.res') `
        "/Fo$(Join-Path $obj 'standin.obj')" "/Fe:$(Join-Path $Out 'monado-service.exe')" `
        advapi32.lib userenv.lib /link /SUBSYSTEM:WINDOWS /ENTRY:mainCRTStartup
    if ($LASTEXITCODE -ne 0) { throw "cl.exe failed (exit code $LASTEXITCODE)." }
}
finally {
    Pop-Location
}
