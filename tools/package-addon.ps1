# Packages the Godot addon as a zip whose root is addons/cantrip, which is the shape the
# Asset Library and every "unzip into your project" instruction expect.
#
#   pwsh tools/package-addon.ps1 [-OutputDirectory <path>]
#
# The version comes from plugin.cfg, so the zip and the plugin can never disagree about it.

[CmdletBinding()]
param(
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$addon = Join-Path $root 'godot/Cantrip.Demo/addons/cantrip'
if (-not (Test-Path $addon)) { throw "The addon is not where it should be: $addon" }

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts' }

$version = (Select-String -Path (Join-Path $addon 'plugin.cfg') -Pattern '^version="(.*)"$').Matches[0].Groups[1].Value
if (-not $version) { throw 'plugin.cfg has no version.' }

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("ge-addon-" + [System.Guid]::NewGuid().ToString('n'))
try {
    $target = Join-Path $staging 'addons/cantrip'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Recurse -Force (Join-Path $addon '*') $target
    Copy-Item -Force (Join-Path $root 'LICENSE') (Join-Path $target 'LICENSE')

    # .uid files are Godot's stable script ids: keeping them means a project that updates the addon
    # does not lose the references its scenes already hold.
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $zip = Join-Path $OutputDirectory "cantrip-godot-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip }

    # Entries are written by hand rather than with Compress-Archive, which records Windows
    # separators. A zip full of "addons\cantrip\..." unpacks as one long filename on Linux
    # and is not what Godot or the Asset Library expect.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $prefix = (Resolve-Path $staging).Path.TrimEnd('\') + '\'
        foreach ($file in Get-ChildItem -Recurse -File $staging) {
            $name = $file.FullName.Substring($prefix.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Output $zip
    (Get-Item $zip).Length.ToString() + ' bytes'
}
finally {
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
}
