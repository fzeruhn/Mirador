#Requires -Version 7.3
<#
.SYNOPSIS
  Starts a Monado build headless and connects an OpenXR app to it: the last check of the nightly (Mirador.md §4.11
  item 55).
.DESCRIPTION
  Starts the build's monado-service.exe with its null compositor and simulated headset (XRT_COMPOSITOR_NULL=1,
  SIMULATED_ENABLE=1: no display, no headset), waits for its pipe, then runs xr-check (tools\xr-check) against it,
  once per -Api. The default, none, creates an OpenXR instance only: the runtime DLL loads with everything it needs,
  and the service takes the app over its pipe. That is all a GitHub runner can show: it has no GPU, and Monado's
  compositor (the null one too) needs a Vulkan device with Windows' sharing extensions, which no software driver tried
  there gave (lavapipe lacks them; Mesa's Dozen didn't load; Mirador_Plan.md). d3d11 and vulkan run a whole session, as
  "Verify build for Windows" does on a PC with a GPU.
  The build must be the OpenXR runtime the loader finds: XR_RUNTIME_JSON is set, but an elevated process's loader
  ignores it (GitHub's runners are elevated), so there the build is made the machine's active runtime first.
  Exit code 0 when every API asked for passed, 1 otherwise.
.EXAMPLE
  tools\check-monado-session.ps1 -Install $env:RUNNER_TEMP\monado-install -XrCheck $env:RUNNER_TEMP\xr-check-build\xr-check.exe
#>
param(
    [Parameter(Mandatory)][string]$Install,
    [Parameter(Mandatory)][string]$XrCheck,
    [string[]]$Api = @('none'),
    [int]$Frames = 60,
    [int]$PipeWaitSeconds = 30,
    [string]$LogDir = $(if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'monado-session' } else { 'monado-session' })
)
$ErrorActionPreference = 'Stop'

$Install = (Resolve-Path -LiteralPath $Install).Path
$service = Join-Path $Install 'bin\monado-service.exe'
$manifest = Join-Path $Install 'openxr_monado.json'
foreach ($file in $service, $manifest, $XrCheck) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "$file is missing." }
}
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$serviceLog = Join-Path $LogDir 'monado-service.log'

# What the machine offers, for reading a failure: its display adapters and the Vulkan drivers registered.
Write-Host 'Display adapters:'
Get-CimInstance Win32_VideoController | ForEach-Object { Write-Host "  $($_.Name) ($($_.DriverVersion))" }
Write-Host 'Vulkan drivers:'
foreach ($key in 'HKLM:\SOFTWARE\Khronos\Vulkan\Drivers', 'HKLM:\SOFTWARE\WOW6432Node\Khronos\Vulkan\Drivers') {
    if (Test-Path $key) { (Get-Item $key).Property | ForEach-Object { Write-Host "  $_" } }
}

# Headless, and only this build: the variables go to the service and to xr-check's OpenXR loader.
$env:XRT_COMPOSITOR_NULL = '1'
$env:SIMULATED_ENABLE = '1'
$env:XR_RUNTIME_JSON = $manifest # an elevated process's loader ignores it: there, make the build the active runtime first
# xr-check loads vulkan-1.dll by name: the build's own copy serves where Windows has none (no GPU driver brought one).
$env:PATH = "$(Join-Path $Install 'bin');$env:PATH"

function Get-MonadoPipe {
    [IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like '*monado_comp_ipc' }
}
if (Get-MonadoPipe) { throw 'Another Monado is running here (its pipe is open).' }

Write-Host "Starting $service headless."
$process = Start-Process -FilePath $service -WorkingDirectory (Split-Path $service) -PassThru -NoNewWindow `
    -RedirectStandardOutput $serviceLog -RedirectStandardError "$serviceLog.err"
$results = [ordered]@{}
try {
    $until = (Get-Date).AddSeconds($PipeWaitSeconds)
    while (-not (Get-MonadoPipe)) {
        if ($process.HasExited) { throw "monado-service.exe ended (exit code $($process.ExitCode)) before it opened its pipe." }
        if ((Get-Date) -gt $until) { throw "monado-service.exe didn't open its pipe within $PipeWaitSeconds s." }
        Start-Sleep -Milliseconds 250
    }
    Write-Host "Monado is up: $(Get-MonadoPipe)"

    foreach ($a in $Api) {
        Write-Host "xr-check --api $a --frames $Frames"
        $output = & $XrCheck --api $a --frames $Frames --timeout 60 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
        $output | ForEach-Object { Write-Host "  $_" }
        $line = $output | Where-Object { $_ -like 'XR-CHECK *' } | Select-Object -Last 1
        $report = if ($line) { $line.Substring(9) | ConvertFrom-Json } else { $null }
        $results[$a] = [ordered]@{
            ok = ($code -eq 0 -and $report -and $report.ok)
            detail = if ($report -and $report.ok -and $a -eq 'none') { "an instance of $($report.runtime), through the service's pipe" }
                     elseif ($report -and $report.ok) { "$($report.frames) frames, $($report.submitted) rendered, with $($report.system)" }
                     elseif ($report) { "$($report.error) (after $($report.frames) frames)" }
                     else { "xr-check gave no result (exit code $code)" }
        }
        if ($process.HasExited) { throw "monado-service.exe ended (exit code $($process.ExitCode)) during the $a session." }
    }
}
catch {
    Write-Host "::error::$($_.Exception.Message)"
    $results['service'] = [ordered]@{ ok = $false; detail = $_.Exception.Message }
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Write-Host '--- monado-service.exe ---'
    foreach ($file in $serviceLog, "$serviceLog.err") {
        if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file -Tail 80 | ForEach-Object { Write-Host "  $_" } }
    }
}

Write-Host '--- Results ---'
foreach ($name in $results.Keys) {
    Write-Host ("{0}: {1}. {2}" -f $name, $(if ($results[$name].ok) { 'Passed' } else { 'Failed' }), $results[$name].detail)
}
if (@($results.Values | Where-Object { -not $_.ok }).Count -gt 0) { exit 1 }
Write-Host 'Every check passed.'
