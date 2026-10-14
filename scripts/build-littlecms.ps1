param([switch]$BuildReferenceTool)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $workspace '.artifacts/native/lcms2'
New-Item -ItemType Directory -Force $cache | Out-Null

function FetchVerified([string]$url, [string]$destination, [string]$hash) {
    if (!(Test-Path -LiteralPath $destination)) { Invoke-WebRequest $url -OutFile $destination }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) {
        throw "Checksum mismatch: $destination"
    }
}

FetchVerified 'https://ziglang.org/download/0.14.1/zig-x86_64-windows-0.14.1.zip' (Join-Path $cache 'zig.zip') '554F5378228923FFD558EAC35E21AF020C73789D87AFEABF4BFD16F2E6FEED2C'
FetchVerified 'https://api.github.com/repos/mm2/Little-CMS/zipball/lcms2.19.1' (Join-Path $cache 'source.zip') 'CF03D84F03E4E90552F61D65F4F1106459D5B71926BF2F2A5E1560F63E87FC14'
# The verified archive is upstream commit 21c582a594fe5279f90c0b93437c398f93bf62b0.
$toolDirectory = Join-Path $cache 'tools-fast'
$sourceDirectory = Join-Path $cache 'source'
New-Item -ItemType Directory -Force $toolDirectory, $sourceDirectory | Out-Null
$compiler = Join-Path $toolDirectory 'zig-x86_64-windows-0.14.1/zig.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    & tar -xf (Join-Path $cache 'zig.zip') -C $toolDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Compiler extraction failed.' }
}
$upstream = Join-Path $sourceDirectory 'mm2-Little-CMS-21c582a'
if (!(Test-Path -LiteralPath (Join-Path $upstream 'include/lcms2.h'))) {
    & tar -xf (Join-Path $cache 'source.zip') -C $sourceDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed.' }
}
$output = Join-Path $cache 'win-x64'
New-Item -ItemType Directory -Force $output | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $upstream 'src') -Filter '*.c' | Sort-Object Name | ForEach-Object FullName)
$arguments = @('cc', '-target', 'x86_64-windows-gnu', '-O2', '-shared', '-DCMS_DLL_BUILD', '-DCMS_NO_PTHREADS', '-I', (Join-Path $upstream 'include')) + $sources + @('-o', (Join-Path $output 'lcms2.dll'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Little CMS compilation failed.' }
Copy-Item -LiteralPath (Join-Path $upstream 'LICENSE') -Destination (Join-Path $output 'LICENSE.txt')

if ($BuildReferenceTool) {
    $common = Join-Path $upstream 'utils/common'
    $arguments = @('cc', '-target', 'x86_64-windows-gnu', '-O2', '-DCMS_NO_PTHREADS', '-I', (Join-Path $upstream 'include'), '-I', $common) + $sources + @(
        (Join-Path $upstream 'utils/transicc/transicc.c'), (Join-Path $common 'vprf.c'), (Join-Path $common 'xgetopt.c'), '-o', (Join-Path $output 'transicc.exe'))
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Little CMS reference CLI compilation failed.' }
}
Get-FileHash -LiteralPath (Join-Path $output 'lcms2.dll') -Algorithm SHA256
