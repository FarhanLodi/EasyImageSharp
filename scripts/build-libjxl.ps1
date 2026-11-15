<#
.SYNOPSIS
Fetches the pinned official win-x64 libjxl 0.12.0 runtime used by EasyImageSharp.JpegXl.

.DESCRIPTION
Downloads the upstream release artefact jxl-x64-windows.zip of libjxl v0.12.0 (built by the JPEG XL project's
own release workflow), verifies its SHA-256 and copies the six runtime DLLs the package ships - jxl, jxl_cms,
jxl_threads and the three Brotli libraries - plus the upstream licence files into win-x64/. Highway and skcms are
linked statically into those DLLs. The DLLs need the Microsoft Visual C++ 2015-2022 runtime (MSVCP140.dll,
VCRUNTIME140.dll), which is present on most Windows installations and is redistributable by Microsoft.

Nothing is installed system-wide and no binary is checked into source control: everything lands under
-WorkDirectory, which defaults to the ignored .artifacts/native/libjxl folder. -ReferenceTools additionally
unpacks the upstream cjxl/djxl command-line tools into tools/, which regenerate the test fixtures.

Linux and macOS consumers should install their platform's libjxl package instead, see
src/EasyImageSharp.Codecs/README.md.
#>
param(
    [string]$WorkDirectory,
    [switch]$ReferenceTools
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$cache = if ($WorkDirectory) { $WorkDirectory } else { Join-Path $workspace '.artifacts/native/libjxl' }
New-Item -ItemType Directory -Force $cache | Out-Null

function FetchVerified([string]$url, [string]$destination, [string]$hash) {
    if (!(Test-Path -LiteralPath $destination)) { Invoke-WebRequest $url -OutFile $destination }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) {
        throw "Checksum mismatch: $destination"
    }
}

# The .7z asset holds the same files as jxl-x64-windows.zip (whose Deflate64 entries Windows' tar cannot read);
# the built-in tar extracts 7-Zip archives, so no extra tool is needed.
$archive = Join-Path $cache 'jxl-x64-windows-0.12.0.7z'
FetchVerified 'https://github.com/libjxl/libjxl/releases/download/v0.12.0/jxl-x64-windows.7z' $archive '9FAF38C27C3DC65805FA991ED06F85076B76D432CBD9386D91B1029769AA8F53'
$extracted = Join-Path $cache 'release'
if (!(Test-Path -LiteralPath (Join-Path $extracted 'x64-windows/bin/jxl.dll'))) {
    New-Item -ItemType Directory -Force $extracted | Out-Null
    & tar -xf $archive -C $extracted
    if ($LASTEXITCODE -ne 0) { throw 'Release extraction failed.' }
}

$release = Join-Path $extracted 'x64-windows'
$output = Join-Path $cache 'win-x64'
New-Item -ItemType Directory -Force $output | Out-Null
foreach ($library in 'jxl', 'jxl_cms', 'jxl_threads', 'brotlicommon', 'brotlidec', 'brotlienc') {
    Copy-Item -LiteralPath (Join-Path $release "bin/$library.dll") -Destination $output -Force
}
foreach ($license in 'libjxl', 'brotli', 'highway', 'skcms') {
    Copy-Item -LiteralPath (Join-Path $release "licenses/LICENSE.$license") -Destination (Join-Path $output "LICENSE.$license.txt") -Force
}

if ($ReferenceTools) {
    $tools = Join-Path $cache 'tools'
    New-Item -ItemType Directory -Force $tools | Out-Null
    Copy-Item -Path (Join-Path $release 'bin/*') -Destination $tools -Force
}

Get-ChildItem -LiteralPath $output -Filter '*.dll' | Sort-Object Name | ForEach-Object { Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 }
