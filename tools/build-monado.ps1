#Requires -Version 5.1
<#
.SYNOPSIS
    Mirador's Monado build recipe: configures, builds and installs a Monado source tree, the same way on CI (the
    nightly) and in the app (a profile built from a branch link).
.DESCRIPTION
    Based on upstream's .gitlab-ci\windows\monado_build.ps1 (forks don't carry one): MSVC found with vswhere, Ninja,
    RelWithDebInfo, the vcpkg toolchain (Monado's vcpkg.json says what it needs), the vcpkg DLLs installed next to
    Monado's (X_VCPKG_APPLOCAL_DEPS_INSTALL). Always also -DXRT_SERVICE_EXECUTABLE=monado-service, without which games
    can't start Monado through Mirador's stand-in (Mirador.md §3, §4.11 item 49); a profile's extra options come after
    Mirador's, so a fork that needs another setting can override one.

    The install is Monado's own Windows layout: openxr_monado.json at the top, its DLL, monado-service.exe and every DLL
    they need in bin\. The Visual C++ runtime DLLs are copied into bin\ too, so the build runs on a PC without the
    Redistributable. build-info.json beside them says what was built: the source URL and commit, the compiler, the
    vcpkg commit, the options and the time.

    Tools: with -Tools, the vcpkg, CMake, Ninja and Git in it (Mirador's pinned copies under %LOCALAPPDATA%\Mirador\tools:
    vcpkg\, cmake\bin\, ninja\, git\cmd\) come first; without it, what is on PATH, and vcpkg from VCPKG_ROOT or
    VCPKG_INSTALLATION_ROOT (GitHub's runners). MSVC is never shipped or downloaded here: vswhere finds Visual Studio
    or its Build Tools with the C++ x64 tools.

    It runs in Windows PowerShell 5.1 as well as PowerShell 7: Mirador runs it with the powershell.exe every Windows
    has. Extra options come one per line in -ExtraCMakeOptionsFile when run that way (a value starting with "-" can't
    be passed as an argument through -File).
.EXAMPLE
    tools\build-monado.ps1 -Source C:\src\monado -Build C:\src\monado\build -Install C:\src\monado\install
.EXAMPLE
    tools\build-monado.ps1 -Source . -Build build -Install install -ExtraCMakeOptions '-DXRT_HAVE_SDL2=OFF'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Source,
    [Parameter(Mandatory)] [string]$Build,
    [Parameter(Mandatory)] [string]$Install,
    [string[]]$ExtraCMakeOptions = @(),
    [string]$ExtraCMakeOptionsFile,
    [string]$Tools
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed (exit code $LASTEXITCODE)."
    }
}

if ($ExtraCMakeOptionsFile) {
    $ExtraCMakeOptions += @(Get-Content -LiteralPath $ExtraCMakeOptionsFile | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
}
foreach ($option in $ExtraCMakeOptions) {
    if ($option -notmatch '^-D[A-Za-z_][A-Za-z0-9_.+-]*(:[A-Z]+)?=') {
        throw "'$option' isn't a CMake setting of the form -DNAME=value."
    }
}

$Source = (Resolve-Path -LiteralPath $Source).Path
if (-not (Test-Path -LiteralPath (Join-Path $Source 'CMakeLists.txt'))) {
    throw "$Source has no CMakeLists.txt: is it a Monado source tree?"
}
$Build = [IO.Path]::GetFullPath($Build)
$Install = [IO.Path]::GetFullPath($Install)

# 1. The tools: Mirador's pinned copies first when given.
if ($Tools) {
    $Tools = (Resolve-Path -LiteralPath $Tools).Path
    $paths = @('cmake\bin', 'ninja', 'git\cmd') | ForEach-Object { Join-Path $Tools $_ } | Where-Object { Test-Path -LiteralPath $_ }
    $env:PATH = (@($paths) + $env:PATH) -join [IO.Path]::PathSeparator
}
$vcpkg = if ($Tools -and (Test-Path -LiteralPath (Join-Path $Tools 'vcpkg\scripts\buildsystems\vcpkg.cmake'))) {
    Join-Path $Tools 'vcpkg'
}
elseif ($env:VCPKG_ROOT) { $env:VCPKG_ROOT }
elseif ($env:VCPKG_INSTALLATION_ROOT) { $env:VCPKG_INSTALLATION_ROOT }
else { $null }
if (-not $vcpkg -or -not (Test-Path -LiteralPath (Join-Path $vcpkg 'scripts\buildsystems\vcpkg.cmake'))) {
    throw 'vcpkg is missing: give -Tools with a vcpkg folder, or set VCPKG_ROOT.'
}
$toolchain = Join-Path $vcpkg 'scripts\buildsystems\vcpkg.cmake'
# vcpkg's own copies of the tools, not its downloads of them, and no telemetry.
$env:VCPKG_ROOT = $vcpkg
$env:VCPKG_DISABLE_METRICS = '1'

# 2. MSVC: a developer shell for x64, entered unless one already is (cl.exe on PATH).
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw 'Visual Studio or its Build Tools are missing (no vswhere.exe). Install them with the C++ x64 tools and the Windows 11 SDK.'
    }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) {
        throw 'Visual Studio has no C++ x64 tools: add "Desktop development with C++" (MSVC x64/x86 build tools and the Windows 11 SDK).'
    }
    # A commit message in the environment can confuse cmd and Enter-VsDevShell (upstream's note).
    $env:CI_COMMIT_DESCRIPTION = ''
    $env:CI_COMMIT_MESSAGE = ''
    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64 -no_logo' | Out-Null
}
foreach ($tool in 'cmake', 'ninja', 'git') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool is missing: give -Tools, or put it on PATH."
    }
}

# vcpkg's own program, built from its source the first time.
$vcpkgExe = Join-Path $vcpkg 'vcpkg.exe'
if (-not (Test-Path -LiteralPath $vcpkgExe)) {
    Invoke-Checked 'Bootstrapping vcpkg' { & (Join-Path $vcpkg 'bootstrap-vcpkg.bat') -disableMetrics }
}

# Monado compiles its shaders with glslangValidator, which its vcpkg.json doesn't ask for (upstream's CI has it from
# the Vulkan SDK). Without one on PATH, vcpkg builds glslang's tools beside the build folder, from the same pinned
# vcpkg (and its binary cache, so only once).
if (-not (Get-Command glslangValidator -ErrorAction SilentlyContinue)) {
    $glslang = "$Build-glslang"
    $glslangTools = Join-Path $glslang 'x64-windows\tools\glslang'
    if (-not (Test-Path -LiteralPath (Join-Path $glslangTools 'glslangValidator.exe'))) {
        Write-Host 'Building glslangValidator with vcpkg'
        Push-Location ([IO.Path]::GetTempPath())
        try {
            Invoke-Checked 'Building glslangValidator' { & $vcpkgExe install 'glslang[tools]:x64-windows' --classic "--x-install-root=$glslang" --disable-metrics }
        }
        finally {
            Pop-Location
        }
    }
    $env:PATH = $glslangTools + [IO.Path]::PathSeparator + $env:PATH
}

# 3. Configure, build, install. A fresh install folder, so nothing of an older build is left in it.
$options = @(
    '-DCMAKE_BUILD_TYPE=RelWithDebInfo'
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain"
    "-DCMAKE_INSTALL_PREFIX=$Install"
    '-DX_VCPKG_APPLOCAL_DEPS_INSTALL=ON'
    '-DXRT_SERVICE_EXECUTABLE=monado-service'
) + $ExtraCMakeOptions

$env:PYTHONUTF8 = '1'

# MSVC reports __cplusplus as 199711L unless told otherwise, so Monado's u_file.cpp takes <experimental/filesystem>,
# which MSVC 14.51 (Visual Studio 2026) no longer has. With the real value it takes <filesystem>, with any MSVC.
# (CMake starts CMAKE_CXX_FLAGS from CXXFLAGS; vcpkg builds its ports in a clean environment, without it.)
$env:CXXFLAGS = (@($env:CXXFLAGS, '/Zc:__cplusplus') | Where-Object { $_ }) -join ' '
Write-Host "Configuring $Source in $Build"
Invoke-Checked 'Configuring Monado' { cmake -S $Source -B $Build -G Ninja @options }
Write-Host 'Building'
Invoke-Checked 'Building Monado' { cmake --build $Build }
if (Test-Path -LiteralPath $Install) {
    Remove-Item -LiteralPath $Install -Recurse -Force
}
Write-Host "Installing into $Install"
Invoke-Checked 'Installing Monado' { cmake --install $Build }

$bin = Join-Path $Install 'bin'
foreach ($file in 'openxr_monado.json', 'bin\openxr_monado.dll', 'bin\monado-service.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $Install $file))) {
        throw "The install has no $file."
    }
}

# 4. The Visual C++ runtime, app-local (the developer shell says where its redistributable DLLs are).
$crt = if ($env:VCToolsRedistDir) {
    Get-ChildItem -Path (Join-Path $env:VCToolsRedistDir 'x64') -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($crt) {
    Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $bin -Force
}
else {
    Write-Warning 'The Visual C++ runtime DLLs weren''t found (no VCToolsRedistDir): this build needs the Visual C++ Redistributable on the PC.'
}

# 5. What was built.
function Get-GitValue([string]$dir, [string[]]$gitArgs) {
    # Continue, not Stop: Windows PowerShell turns a native program's redirected error output into errors.
    $ErrorActionPreference = 'Continue'
    $value = git -C $dir @gitArgs 2>$null
    if ($LASTEXITCODE -eq 0 -and $value) { "$value".Trim() } else { $null }
}
$compiler = & {
    $ErrorActionPreference = 'Continue'
    (& cl.exe 2>&1 | Select-Object -First 1 | ForEach-Object { "$_" } | Out-String).Trim()
}
$info = [ordered]@{
    format         = 1
    sourceUrl      = Get-GitValue $Source @('remote', 'get-url', 'origin')
    commit         = Get-GitValue $Source @('rev-parse', 'HEAD')
    describe       = Get-GitValue $Source @('describe', '--always', '--dirty')
    compiler       = $compiler
    msvcVersion    = $env:VCToolsVersion
    windowsSdk     = $env:WindowsSDKVersion -replace '\\$', ''
    cmakeVersion   = ((cmake --version | Select-Object -First 1) -replace '^cmake version ', '').Trim()
    vcpkgCommit    = Get-GitValue $vcpkg @('rev-parse', 'HEAD')
    buildType      = 'RelWithDebInfo'
    options        = @($options | Where-Object { $_ -notlike '-DCMAKE_TOOLCHAIN_FILE=*' -and $_ -notlike '-DCMAKE_INSTALL_PREFIX=*' })
    cxxFlags       = $env:CXXFLAGS
    builtAt        = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
}
# UTF-8 without a byte order mark, in both PowerShells.
[IO.File]::WriteAllText((Join-Path $Install 'build-info.json'), ($info | ConvertTo-Json -Depth 3), (New-Object Text.UTF8Encoding $false))

Write-Host "Monado $($info.describe) installed in $Install"
