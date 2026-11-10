# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0
#
# Configure, build and test the direct-mode layer (Mirador's tools\build.ps1 and tools\build-bundle.ps1 run it), with
# Mirador's tools\build-cmake.ps1: a Visual Studio developer shell, vcpkg's x64-windows-static triplet, Ninja.
#   pwsh -NoProfile -File layer\build.ps1 -Configure -Build -Test
param(
    [switch]$Configure,
    [switch]$Build,
    [switch]$Test,
    [string]$VsPath,
    [string]$BuildDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\layer-build'),
    [string[]]$Extra = @()
)
$ErrorActionPreference = 'Stop'
& (Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\build-cmake.ps1') -Source $PSScriptRoot -BuildDir $BuildDir `
    -Configure:$Configure -Build:$Build -Test:$Test -VsPath $VsPath -Extra $Extra
