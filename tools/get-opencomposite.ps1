#Requires -Version 7.3
<#
.SYNOPSIS
    Fetches OpenComposite's 64-bit build from Mirador's fork's release, for the bundle (tools\build-bundle.ps1 -OpenComposite).
.DESCRIPTION
    OpenComposite turns OpenVR into OpenXR, so OpenVR (SteamVR) games run on Monado (Mirador.md section 4.6). It is
    GPL-3.0-or-later and ships as its own DLL, which Mirador never links or loads: games do. The fork,
    github.com/fzeruhn/OpenComposite, builds it in its release workflow and attaches OpenComposite-windows.zip, which
    holds an OpenComposite\ folder: bin\vrclient_x64.dll, bin\version.txt (empty), SOURCE.txt (line 1: version and
    commit), LICENSE.txt and the licences and notes of what is built into the DLL.

    The archive is downloaded from GitHub over HTTPS (or taken from -Zip) and its SHA-256 must be -Sha256: anything
    else is refused before a byte is unpacked. It is unpacked into a temporary folder (every entry must stay inside
    it), which must hold OpenComposite\bin\vrclient_x64.dll, OpenComposite\SOURCE.txt and OpenComposite\LICENSE.txt;
    then -Out is replaced by that OpenComposite\ folder.

    Pinning another release means giving its -Tag and the -Sha256 of its OpenComposite-windows.zip. The default pin,
    v2026.11.06, has no such file as a release asset (it is inside that release's OpenComposite.zip), so the
    download says so; take the file out of that archive and give it as -Zip.
.EXAMPLE
    tools\get-opencomposite.ps1
.EXAMPLE
    tools\get-opencomposite.ps1 -Zip C:\Downloads\OpenComposite-windows.zip
#>
[CmdletBinding()]
param(
    [string]$Tag = 'v2026.11.06',
    [string]$Sha256 = 'aef3d723877b98971698218451b516d0b0f92b71df3ca13594140590d008eb29',
    [string]$Zip,
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\opencomposite')
)

$ErrorActionPreference = 'Stop'

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "mirador-opencomposite-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
try {
    # 1. The archive: -Zip, or the release's file from GitHub.
    if ($Zip) {
        $archive = (Resolve-Path -LiteralPath $Zip).Path
    }
    else {
        $url = "https://github.com/fzeruhn/OpenComposite/releases/download/$Tag/OpenComposite-windows.zip"
        $archive = Join-Path $tempRoot 'OpenComposite-windows.zip'
        Write-Host "Downloading $url"
        try {
            Invoke-WebRequest -Uri $url -OutFile $archive -MaximumRedirection 5
        }
        catch {
            if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) {
                throw "The release $Tag of github.com/fzeruhn/OpenComposite has no OpenComposite-windows.zip ($url answered 404). Take that file out of the release's OpenComposite.zip and give it as -Zip, or pin another release with -Tag and -Sha256."
            }
            throw
        }
    }

    # 2. Its SHA-256, before anything is unpacked.
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        throw "Refusing $archive`: its SHA-256 is $actual, not the pinned $Sha256."
    }

    # 3. Unpacked into a temporary folder, each entry checked to stay inside it.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $unpacked = Join-Path $tempRoot 'unpacked'
    New-Item -ItemType Directory -Force -Path $unpacked | Out-Null
    $root = [IO.Path]::GetFullPath($unpacked).TrimEnd('\') + '\'
    $zipFile = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zipFile.Entries) {
            $target = [IO.Path]::GetFullPath([IO.Path]::Combine($unpacked, $entry.FullName))
            if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing $archive`: the entry '$($entry.FullName)' would be unpacked outside the target folder."
            }
        }
        foreach ($entry in $zipFile.Entries) {
            $target = [IO.Path]::GetFullPath([IO.Path]::Combine($unpacked, $entry.FullName))
            if ($entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')) {
                New-Item -ItemType Directory -Force -Path $target | Out-Null
            }
            else {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        }
    }
    finally {
        $zipFile.Dispose()
    }

    # 4. What the bundle needs.
    $folder = Join-Path $unpacked 'OpenComposite'
    foreach ($needed in 'bin\vrclient_x64.dll', 'SOURCE.txt', 'LICENSE.txt') {
        if (-not (Test-Path -LiteralPath (Join-Path $folder $needed) -PathType Leaf)) {
            throw "Refusing $archive`: it has no OpenComposite\$needed."
        }
    }

    # 5. -Out replaced by it.
    if (Test-Path -LiteralPath $Out) {
        Remove-Item -LiteralPath $Out -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
    Move-Item -LiteralPath $folder -Destination $Out

    Write-Host "OpenComposite: $Out"
    Write-Host "  $((Get-Content -LiteralPath (Join-Path $Out 'SOURCE.txt') -TotalCount 1).Trim())"
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
