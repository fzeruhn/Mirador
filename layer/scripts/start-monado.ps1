# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

<#
.SYNOPSIS
Starts Monado for the Pimax Dream Air through the Vulkan layer, in this console.

.DESCRIPTION
Run it from a normal (not elevated) PowerShell, with Pimax and SteamVR stopped. It

  1. refuses to run elevated (an elevated Monado service would turn away unelevated apps, and this script never sets
     IPC_RELAXED_CONNECTION_SECURITY to get around that);
  2. checks that nothing holds the display, the board and eye tracker, or the Lighthouse receiver (Pimax's programs,
     SteamVR's), naming what runs, and that Monado is not running already (one at a time);
  3. sets the environment Monado needs for the layer (see the list printed by -DryRun), in this process only
     (VK_LOADER_LAYERS_ALLOW=VK_LAYER_DREAMAIR_nvapi_direct replaces an inherited value; it keeps SteamVR's tracking
     driver, loaded inside Monado, from switching the layer off), and removes inherited variables that would change
     what this script guarantees (DREAMAIR_VK_LAYER_IGNORE_HOLDERS unless -LayerIgnoreHolders,
     DREAMAIR_VK_LAYER_TEST_NO_EDID, DISABLE_DREAMAIR_VK_LAYER, IPC_RELAXED_CONNECTION_SECURITY,
     XRT_COMPOSITOR_FORCE_NVAPI_DIRECT), naming any it
     removed; all of it is put back when Monado exits;
  4. finds monado-service.exe next to the runtime DLL of the active OpenXR runtime
     (HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime), or uses -Service;
  5. runs it in this console. Quit it with a key press in this window, not Ctrl+C: Ctrl+C ends it without its clean
     shutdown.

It does not stop Pimax, switch the OpenXR runtime or upload the eye tracker's geometry.

.PARAMETER Service
Path of monado-service.exe. Default: next to the active OpenXR runtime's DLL.

.PARAMETER SkipHolderChecks
Skip this script's own holder checks (tests only). The layer's own check still runs, and so does the check for a
Monado that is already running.

.PARAMETER LayerIgnoreHolders
Also set DREAMAIR_VK_LAYER_IGNORE_HOLDERS=1, which skips the layer's own holder check (tests only).

.PARAMETER Log
Write Monado's output (and the layer's lines) to this file instead of the window. The key press still works. A
relative path is relative to the folder you start the script from; its folder must exist.

.PARAMETER DryRun
Print what would be checked, set and started, and start nothing. Also -WhatIf. Exit code 1 if a real run would stop.

.EXAMPLE
.\start-monado.ps1

.EXAMPLE
.\start-monado.ps1 -Service "D:\Monado\bin\monado-service.exe" -Log "$env:TEMP\monado.log"
#>
[CmdletBinding()]
param(
    [string]$Service,
    [switch]$SkipHolderChecks,
    [switch]$LayerIgnoreHolders,
    [string]$Log,
    [Alias('WhatIf')]
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'

# Design section 4, "Holder checks": who holds what. Process names as Get-Process takes them (no .exe).
# core\holders.cpp, which the layer and the check tool use, carries the same lists.
$holders = [ordered]@{
    'the display (direct mode)' = 'pi_server', 'vrcompositor'
    'the board and eye tracker' = 'pi_server', 'PimaxClient', 'PiPlayService', 'PiPlatformService_64', 'pi_overlay',
                                  'vrss_gaze_provider'
    'the Lighthouse receiver'   = 'vrserver', 'vrmonitor', 'OVRLighthouseManager'
}

# Design section 9, the one list of Monado's variables. IPC_RELAXED_CONNECTION_SECURITY is not on it and never will be
# here: only Finn's launcher sets it, with -Elevated.
# VK_LOADER_LAYERS_ALLOW: with STEAMVR_LH_ENABLE, SteamVR's Lighthouse driver runs inside Monado and sets
# VK_LOADER_LAYERS_DISABLE at run time, which switches off every implicit layer, ours too; an allow filter beats a
# disable filter (design section 5). It replaces any value inherited from the shell.
$vars = [ordered]@{
    DREAMAIR_VK_LAYER                          = '1'
    DISABLE_RTSS_LAYER                         = '1'
    VK_LOADER_LAYERS_ALLOW                     = 'VK_LAYER_DREAMAIR_nvapi_direct'
    XRT_COMPOSITOR_FORCE_VK_DISPLAY            = '0'
    XRT_COMPOSITOR_USE_PRESENT_WAIT            = '1'
    U_PACING_COMP_MIN_TIME_MS                  = '6'
    U_PACING_COMP_PRESENT_TO_DISPLAY_OFFSET_MS = '8.9'
    STEAMVR_LH_ENABLE                          = '1'
}
if ($LayerIgnoreHolders) {
    $vars['DREAMAIR_VK_LAYER_IGNORE_HOLDERS'] = '1'
}

# Variables a shell may have left set that change what this script guarantees: the layer's holder check or its
# EDID routes skipped (tests only), the layer switched off, any signed-in user's processes let into the
# service, Monado's own nvapi_direct target instead of the layer. They are kept from Monado unless a switch of this
# script asks for one.
$scrub = @('DREAMAIR_VK_LAYER_TEST_NO_EDID', 'DISABLE_DREAMAIR_VK_LAYER', 'IPC_RELAXED_CONNECTION_SECURITY',
           'XRT_COMPOSITOR_FORCE_NVAPI_DIRECT')
if (-not $LayerIgnoreHolders) {
    $scrub = @('DREAMAIR_VK_LAYER_IGNORE_HOLDERS') + $scrub
}
$inherited = @($scrub | Where-Object { $null -ne [Environment]::GetEnvironmentVariable($_, 'Process') })

$script:refusals = 0

# Stops the run with a message; in a dry run it only counts the problem, so the rest of the plan still prints.
function Refuse([string]$Message) {
    Write-Host "error: $Message" -ForegroundColor Red
    if (-not $DryRun) { exit 1 }
    $script:refusals++
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# The value of an HKLM\SOFTWARE value in the 64-bit registry view (the one the loaders read), or $null.
function Get-HklmValue([string]$SubKey, [string]$Name) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    try {
        $key = $base.OpenSubKey($SubKey)
        if (-not $key) { return $null }
        try { return $key.GetValue($Name) } finally { $key.Dispose() }
    } finally {
        $base.Dispose()
    }
}

# Is the layer registered with the Vulkan loader (install.ps1), or found through a layer-path variable (tests)?
function Test-LayerRegistered {
    if ($env:VK_IMPLICIT_LAYER_PATH -or $env:VK_ADD_IMPLICIT_LAYER_PATH) { return $true }
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    try {
        $key = $base.OpenSubKey('SOFTWARE\Khronos\Vulkan\ImplicitLayers')
        if (-not $key) { return $false }
        try {
            foreach ($name in $key.GetValueNames()) {
                if ($name -match '(^|[\\/])dreamair_vk_layer\.json$') { return $true }
            }
            return $false
        } finally { $key.Dispose() }
    } finally {
        $base.Dispose()
    }
}

# monado-service.exe next to the runtime DLL of the active OpenXR runtime, or $null after a message.
function Find-MonadoService {
    $json = Get-HklmValue 'SOFTWARE\Khronos\OpenXR\1' 'ActiveRuntime'
    if (-not $json) {
        Refuse ('no OpenXR runtime is active (HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime is not set); make Monado ' +
            'the active runtime, or give the path of monado-service.exe with -Service')
        return $null
    }
    Write-Host "active OpenXR runtime: $json"
    if (-not (Test-Path -LiteralPath $json -PathType Leaf)) {
        Refuse ("the active OpenXR runtime file does not exist: $json " +
            '(give the path of monado-service.exe with -Service)')
        return $null
    }
    $library = $null
    try {
        $library = (Get-Content -LiteralPath $json -Raw | ConvertFrom-Json).runtime.library_path
    } catch {
        Refuse "cannot read the active OpenXR runtime file ${json}: $($_.Exception.Message)"
        return $null
    }
    if (-not $library) {
        Refuse "the active OpenXR runtime file names no runtime.library_path: $json"
        return $null
    }
    # A relative library_path is relative to the JSON file.
    if (-not [IO.Path]::IsPathRooted($library)) {
        $library = Join-Path (Split-Path -Parent $json) $library
    }
    $library = [IO.Path]::GetFullPath($library)
    $folder = Split-Path -Parent $library
    # Next to the runtime DLL (an installed Monado), else in the sibling "service" folder (a Monado build tree keeps
    # the OpenXR runtime in targets\openxr and the service in targets\service).
    $candidates = @(
        (Join-Path $folder 'monado-service.exe'),
        (Join-Path (Join-Path (Split-Path -Parent $folder) 'service') 'monado-service.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    Refuse ("monado-service.exe is not next to the active OpenXR runtime's DLL ($library). Is Monado the active " +
        'runtime? If not, make it the active runtime, or give the path of monado-service.exe with -Service')
    return $null
}

if ($DryRun) {
    Write-Host 'dry run: nothing is started, changed or written'
}

# 1. Never elevated (design section 2, goal 2).
if (Test-Admin) {
    Write-Host ('error: this PowerShell is elevated (run as administrator). Monado must start unelevated here: an ' +
        'elevated service refuses unelevated apps, unless IPC_RELAXED_CONNECTION_SECURITY is set, which this script ' +
        'never does. Open a normal PowerShell and run it again.') -ForegroundColor Red
    exit 1
}

# 2. Is the layer there at all?
if (Test-LayerRegistered) {
    Write-Host 'Vulkan layer: registered'
} else {
    Write-Host ('warning: the Vulkan layer VK_LAYER_DREAMAIR_nvapi_direct is not registered (run install.ps1 from an ' +
        'elevated PowerShell), so Monado will not find the Dream Air display.') -ForegroundColor Yellow
}

# 3. Holder checks (this script's own; the layer checks the display holders again).
if ($SkipHolderChecks) {
    Write-Host 'holder checks: skipped (-SkipHolderChecks)'
} else {
    $running = @(Get-Process | ForEach-Object { $_.ProcessName })
    $lines = @()
    foreach ($what in $holders.Keys) {
        $found = @($holders[$what] | Where-Object { $running -contains $_ } | ForEach-Object { "$_.exe" })
        if ($found.Count -gt 0) {
            $lines += ('  {0}: held by {1}' -f $what, ($found -join ', '))
        }
    }
    if ($lines.Count -gt 0) {
        Refuse ("the headset is in use:`n" + ($lines -join "`n") + "`n" +
            'Quit Pimax Play (tray icon > Exit) and SteamVR / OVR Lighthouse Manager. If a Pimax program is still ' +
            "listed, stop Pimax's service from an elevated PowerShell with 'Stop-Service PiServiceLauncher' " +
            '(start it again when you are done). Then run this script again.')
    } else {
        Write-Host 'holder checks: nothing holds the display, the board and eye tracker, or the Lighthouse receiver'
    }
}

# 3b. One Monado at a time, -SkipHolderChecks or not: a second service would open the board and, through the layer, call
# EnableDirectMode and BindDevice while the first drives the headset (design section 11, risk 5), before failing on
# Monado's pipe.
$monado = @(Get-Process -Name 'monado-service' -ErrorAction SilentlyContinue)
if ($monado.Count -gt 0) {
    $ids = ($monado | ForEach-Object { $_.Id }) -join ', '
    Refuse ("Monado is already running (monado-service.exe, process $ids). Use that one, or quit it first with a " +
        'key press in its window, then run this script again.')
} else {
    Write-Host 'Monado: not running yet'
}

# 4. monado-service.exe
$serviceExe = $null
if ($Service) {
    if (Test-Path -LiteralPath $Service -PathType Leaf) {
        $serviceExe = (Resolve-Path -LiteralPath $Service).Path
    } else {
        Refuse "no such file: $Service"
    }
} else {
    $serviceExe = Find-MonadoService
}

# -Log: relative to where the script was started, before it changes to the service's folder.
$logPath = $null
if ($Log) {
    $logPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Log)
    $logDir = Split-Path -Parent $logPath
    if (-not $logDir -or -not (Test-Path -LiteralPath $logDir -PathType Container)) {
        Refuse "the folder of -Log does not exist: $logDir (make it first, or give another file)"
    }
}

$removedLine = if ($inherited.Count -gt 0) {
    "removed from Monado's environment (inherited, and not asked for by a switch): " + ($inherited -join ', ')
}

# 5. Run it.
if ($DryRun) {
    Write-Host 'environment (this process only, restored afterwards):'
    foreach ($k in $vars.Keys) { Write-Host "  set $k=$($vars[$k])" }
    if ($removedLine) { Write-Host "  would be $removedLine" }
    if ($serviceExe) {
        Write-Host "would start: $serviceExe"
        $to = if ($logPath) { $logPath } else { 'this window' }
        Write-Host "  in its own folder, in this console; output to $to"
    }
    Write-Host 'would say: quit with a key press here, not Ctrl+C'
    if ($script:refusals -gt 0) {
        Write-Host "dry run: a real run would have stopped ($($script:refusals) problem(s) above)" `
            -ForegroundColor Yellow
        exit 1
    }
    Write-Host 'dry run: a real run would start Monado as above'
    exit 0
}

# Everything this run changes in the process's environment is saved first and put back in the finally below (a script
# run with .\start-monado.ps1 shares the caller's environment).
$saved = @{}
foreach ($k in @($vars.Keys) + $inherited) {
    $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process')
}
$exitCode = 0
$pushed = $false
$p = $null

# Ctrl+C in this window does not stop a Monado started by Start-Process: it only ends this script's wait, and Monado
# went on running in the background (2026-10-31). So the finally below ends a Monado that is still running, as Ctrl+C
# was meant to (without its clean shutdown). Ctrl+Break to its process group would also reach this window when they
# share a group (seen: PowerShell then drops into its debugger), so the process is ended directly.
function Stop-Monado($proc) {
    Write-Host 'Ctrl+C: ending monado-service'
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($proc.Id)" -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}

try {
    foreach ($k in $vars.Keys) {
        [Environment]::SetEnvironmentVariable($k, $vars[$k], 'Process')
    }
    foreach ($k in $inherited) {
        [Environment]::SetEnvironmentVariable($k, $null, 'Process')
    }
    if ($removedLine) { Write-Host $removedLine }
    Push-Location -LiteralPath (Split-Path -Parent $serviceExe)
    $pushed = $true
    Write-Host "starting $serviceExe"
    Write-Host ('Monado is running in this window: quit with a key press here (Ctrl+C also stops it, ' +
        'but skips its clean shutdown).')
    Write-Host 'The layer logs to the window (or -Log) and to %LOCALAPPDATA%\DreamAirVkLayer\layer.log.'
    # Monado's own errors and exit code are reported below, not turned into this script's errors.
    $ErrorActionPreference = 'Continue'
    if ($logPath) {
        Write-Host "output goes to $logPath"
        # cmd does the redirection, so stdout and stderr land in one plain-text file in any PowerShell version.
        $command = '"{0}" > "{1}" 2>&1' -f $serviceExe, $logPath
        $p = Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" -ArgumentList ('/s /c "' + $command + '"') `
            -WorkingDirectory (Split-Path -Parent $serviceExe) -NoNewWindow -PassThru
    } else {
        $p = Start-Process -FilePath $serviceExe -WorkingDirectory (Split-Path -Parent $serviceExe) -NoNewWindow -PassThru
    }
    $null = $p.Handle # kept open, so the exit code can still be read after the process ends
    # Polled, not waited on, so that Ctrl+C ends the wait here (and the finally then stops Monado).
    while (-not $p.HasExited) { Start-Sleep -Milliseconds 200 }
    $exitCode = $p.ExitCode
    Write-Host "monado-service exited ($exitCode)"
} finally {
    if ($p -and -not $p.HasExited) { Stop-Monado $p }
    if ($pushed) { Pop-Location }
    foreach ($k in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process')
    }
}
exit $exitCode
