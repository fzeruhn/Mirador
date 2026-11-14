#Requires -Version 7.3
<#
.SYNOPSIS
    Checks a Monado build made by tools\build-monado.ps1, as Mirador does after every build (Mirador_Plan.md Task 59),
    and writes checks.json into the install folder. Exits 1 when a check failed.
.DESCRIPTION
    The same checks, and the same checks.json, as src\Mirador.Core\Build\BuildChecks.cs: the install's files
    (openxr_monado.json, bin\openxr_monado.dll, bin\monado-service.exe), the runtime manifest naming that DLL, every
    DLL the runtime and the service need (and those need) found in bin\ or Windows' own folder, the service setting in
    the build's CMakeCache.txt, and Monado's unit tests (ctest). Here the DLLs' imports come from dumpbin (entered
    through vswhere when it isn't on PATH); the app reads them itself. Nothing opens a window.
.EXAMPLE
    tools\check-monado-build.ps1 -Install C:\src\monado\install -Build C:\src\monado\build
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Install,
    [string]$Build,
    [string]$Commit
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$Install = (Resolve-Path -LiteralPath $Install).Path
$bin = Join-Path $Install 'bin'
$checks = [System.Collections.Generic.List[object]]::new()

function Add-Check([string]$name, [string]$outcome, [string]$detail) {
    $checks.Add([ordered]@{ name = $name; outcome = $outcome; detail = $detail })
    Write-Host "Check: ${name}: $outcome. $detail"
}

# Files.
$missing = @('openxr_monado.json', 'bin\openxr_monado.dll', 'bin\monado-service.exe') | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Install $_)) }
if ($missing) { Add-Check 'Files' 'Failed' "Missing: $($missing -join ', ')." }
else { Add-Check 'Files' 'Passed' 'The runtime manifest, its DLL and monado-service.exe are there.' }

# The runtime manifest.
$manifest = Join-Path $Install 'openxr_monado.json'
try {
    $library = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).runtime.library_path
    $libraryPath = [IO.Path]::GetFullPath($library, $Install)
    if (-not $library) { Add-Check 'Runtime manifest' 'Failed' "The file $manifest has no runtime.library_path." }
    elseif ([IO.Path]::IsPathRooted($library)) { Add-Check 'Runtime manifest' 'Failed' "It names $library by its full path: once unpacked elsewhere it must name its DLL relative to itself (the helper refuses it)." }
    elseif ($libraryPath -ne (Join-Path $Install 'bin\openxr_monado.dll')) { Add-Check 'Runtime manifest' 'Failed' "It names $libraryPath, not the install's bin\openxr_monado.dll." }
    elseif (-not (Test-Path -LiteralPath $libraryPath)) { Add-Check 'Runtime manifest' 'Failed' "The file $manifest points to $libraryPath, which doesn't exist." }
    else {
        # Any other path in it (Monado's MND_libmonado_path) must stay inside the install, relative, as the helper requires.
        $runtime = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).runtime
        $outside = $runtime.PSObject.Properties | Where-Object { $_.Name -like '*_path' -and $_.Value -is [string] } | Where-Object {
            [IO.Path]::IsPathRooted($_.Value) -or -not [IO.Path]::GetFullPath($_.Value, $Install).StartsWith((Join-Path $Install ''), [StringComparison]::OrdinalIgnoreCase)
        }
        if ($outside) { Add-Check 'Runtime manifest' 'Failed' "It names files outside the install, or by their full path: $(($outside | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', ')." }
        else { Add-Check 'Runtime manifest' 'Passed' 'It names bin\openxr_monado.dll, and nothing outside the install.' }
    }
}
catch {
    Add-Check 'Runtime manifest' 'Failed' "The file $manifest can't be read as JSON: $($_.Exception.Message)"
}

# DLLs, through dumpbin.
if (-not (Get-Command dumpbin.exe -ErrorAction SilentlyContinue)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64 -no_logo' | Out-Null
}
$system = [Environment]::SystemDirectory
$start = @('openxr_monado.dll', 'monado-service.exe') | ForEach-Object { Join-Path $bin $_ } | Where-Object { Test-Path -LiteralPath $_ }
if (-not $start) {
    Add-Check 'DLLs' 'Skipped' 'Nothing to check: the runtime DLL and the service are missing.'
}
else {
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $notFound = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue = [System.Collections.Generic.Queue[string]]::new([string[]]$start)
    while ($queue.Count -gt 0) {
        $file = $queue.Dequeue()
        # dumpbin /dependents lists the DLLs after "Image has the following dependencies:", up to the blank line
        # before "Summary" (delay-loaded ones come under their own heading, and are left out as the app does).
        $lines = dumpbin.exe /nologo /dependents $file
        $inList = $false
        foreach ($line in $lines) {
            if ($line -match 'Image has the following dependencies:') { $inList = $true; continue }
            if ($inList -and $line -match '^\s+(\S+\.dll)\s*$') {
                $dll = $Matches[1]
                if (-not $seen.Add($dll) -or $dll -like 'api-ms-win-*' -or $dll -like 'ext-ms-*') { continue }
                $local = Join-Path $bin $dll
                if (Test-Path -LiteralPath $local) { $queue.Enqueue($local) }
                elseif (-not (Test-Path -LiteralPath (Join-Path $system $dll))) { [void]$notFound.Add("$dll (needed by $(Split-Path -Leaf $file))") }
            }
            elseif ($inList -and $line -match 'delay load|Summary') { $inList = $false }
        }
    }
    if ($notFound.Count -gt 0) { Add-Check 'DLLs' 'Failed' "Not found: $($notFound -join ', ')." }
    else { Add-Check 'DLLs' 'Passed' "All $($seen.Count) DLLs needed are in bin or Windows." }
}

# The service setting, and the unit tests.
if (-not $Build) {
    Add-Check 'Service setting' 'Skipped' 'No build folder given.'
    Add-Check 'Unit tests' 'Skipped' 'No build folder given.'
}
else {
    $Build = (Resolve-Path -LiteralPath $Build).Path
    $cache = Join-Path $Build 'CMakeCache.txt'
    $value = if (Test-Path -LiteralPath $cache) {
        (Select-String -LiteralPath $cache -Pattern '^XRT_SERVICE_EXECUTABLE:[A-Z]+=(.*)$' | Select-Object -First 1).Matches.Groups[1].Value
    }
    if ($value -in 'mirador-standin', 'monado-service') { Add-Check 'Service setting' 'Passed' "XRT_SERVICE_EXECUTABLE is $($value): games can start Monado through Mirador." }
    else { Add-Check 'Service setting' 'Failed' "XRT_SERVICE_EXECUTABLE is $(if ($value) { "'$value'" } else { 'not set' }): games can't start this Monado by themselves." }

    $output = ctest --test-dir $Build --output-on-failure --timeout 120 2>&1
    $code = $LASTEXITCODE
    $output | Write-Host
    $failed = $output | Where-Object { "$_" -match '^\s*\d+ - (.+\((Failed|Timeout|SEGFAULT|Exception)\).*)$' } | ForEach-Object { ("$_" -split ' - ', 2)[1].Trim() }
    # ctest exits 0 with no tests at all: for the weekly build that is a build nothing tested, so it isn't published.
    if ($code -eq 0 -and ($output | Where-Object { "$_" -match 'No tests were found' })) { Add-Check 'Unit tests' 'Failed' 'ctest found no tests in this build.' }
    elseif ($code -eq 0) { Add-Check 'Unit tests' 'Passed' 'ctest passed.' }
    elseif ($failed) { Add-Check 'Unit tests' 'Failed' "Failed: $($failed -join ', ')." }
    else { Add-Check 'Unit tests' 'Failed' "ctest failed (exit code $code)." }
}

if (-not $Commit) {
    $info = Join-Path $Install 'build-info.json'
    if (Test-Path -LiteralPath $info) { $Commit = (Get-Content -LiteralPath $info -Raw | ConvertFrom-Json).commit }
}
[ordered]@{
    format    = 1
    commit    = $Commit
    checkedAt = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssK')
    checks    = $checks
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Install 'checks.json') -Encoding utf8NoBOM

if ($checks | Where-Object { $_.outcome -eq 'Failed' }) {
    Write-Host 'Some checks failed.'
    exit 1
}
Write-Host 'All checks passed.'
