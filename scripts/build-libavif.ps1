<#
.SYNOPSIS
Builds the reproducible win-x64 libavif runtime (avif.dll) used by EasyImageSharp.Avif.

.DESCRIPTION
Downloads SHA-256-pinned Zig 0.14.1 (the C toolchain, the same one scripts/build-littlecms.ps1 uses), Ninja
1.12.1, the upstream libavif v1.4.2 source and the official libaom 3.14.1 release tarball, then builds libaom as a
static library and libavif as a DLL that links it (AV1 encode and decode). Nothing is installed system-wide and
no binary is checked into source control: everything lands under -WorkDirectory, which defaults to the ignored
.artifacts/native/libavif folder, and the result is win-x64/avif.dll plus the upstream licence files.

CMake 3.22 or newer must be available (pass -CMake when it is not on PATH). libaom is configured with
AOM_TARGET_CPU=generic, i.e. portable C without NASM-assembled SIMD kernels; this is correct but slower than a
distribution build. Linux and macOS consumers should install their platform's libavif package instead, see
src/EasyImageSharp.Codecs/README.md.

.EXAMPLE
pwsh scripts/build-libavif.ps1 -CMake "D:/Program Files/CMake/bin/cmake.exe"
#>
param(
    [string]$WorkDirectory,
    [string]$CMake = 'cmake',
    # libaom generates its CPU-dispatch tables with Perl; Git for Windows ships one.
    [string]$Perl,
    [int]$Jobs = 0
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
# The path is used as given: Windows PowerShell's GetFullPath expands 8.3 short names, and spaces or very long
# paths break the native toolchain (CMake limits object file paths to 250 characters).
$cache = if ($WorkDirectory) { $WorkDirectory } else { Join-Path $workspace '.artifacts/native/libavif' }
New-Item -ItemType Directory -Force $cache | Out-Null

function FetchVerified([string]$url, [string]$destination, [string]$hash) {
    if (!(Test-Path -LiteralPath $destination)) { Invoke-WebRequest $url -OutFile $destination }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) {
        throw "Checksum mismatch: $destination"
    }
}

function ExpandOnce([string]$archive, [string]$directory, [string]$marker) {
    if (!(Test-Path -LiteralPath (Join-Path $directory $marker))) {
        New-Item -ItemType Directory -Force $directory | Out-Null
        & tar -xf $archive -C $directory
        if ($LASTEXITCODE -ne 0) { throw "Extraction failed: $archive" }
    }
}

function Invoke-Checked([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE." }
}

$downloads = Join-Path $cache 'downloads'
New-Item -ItemType Directory -Force $downloads | Out-Null
FetchVerified 'https://ziglang.org/download/0.14.1/zig-x86_64-windows-0.14.1.zip' (Join-Path $downloads 'zig.zip') '554F5378228923FFD558EAC35E21AF020C73789D87AFEABF4BFD16F2E6FEED2C'
FetchVerified 'https://github.com/ninja-build/ninja/releases/download/v1.12.1/ninja-win.zip' (Join-Path $downloads 'ninja-win.zip') 'F550FEC705B6D6FF58F2DB3C374C2277A37691678D6ABA463ADCBB129108467A'
FetchVerified 'https://github.com/AOMediaCodec/libavif/archive/refs/tags/v1.4.2.tar.gz' (Join-Path $downloads 'libavif-1.4.2.tar.gz') '2B645287340BA5A631D268B551DC2D72BD73AC33335962DD36DCDB6D8366921D'
# Official AOM release tarball (signature: libaom-3.14.1.tar.gz.asc next to it); the tag libavif v1.4.2 pins in ext/aom.cmd.
FetchVerified 'https://storage.googleapis.com/aom-releases/libaom-3.14.1.tar.gz' (Join-Path $downloads 'libaom-3.14.1.tar.gz') '44BF90DBD23E734D50E70A8C41C285193922938BD0D3BC2EE56764D181D55EF5'

$tools = Join-Path $cache 'tools'
ExpandOnce (Join-Path $downloads 'zig.zip') $tools 'zig-x86_64-windows-0.14.1/zig.exe'
ExpandOnce (Join-Path $downloads 'ninja-win.zip') (Join-Path $tools 'ninja') 'ninja.exe'
$sources = Join-Path $cache 'source'
ExpandOnce (Join-Path $downloads 'libavif-1.4.2.tar.gz') $sources 'libavif-1.4.2/include/avif/avif.h'
ExpandOnce (Join-Path $downloads 'libaom-3.14.1.tar.gz') $sources 'libaom-3.14.1/aom/aom.h'

$zig = (Join-Path $tools 'zig-x86_64-windows-0.14.1/zig.exe').Replace('\', '/')
$ninja = (Join-Path $tools 'ninja/ninja.exe').Replace('\', '/')
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $cache 'zig-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $cache 'zig-cache'
# Old CMake minimums in dependencies are rejected by CMake 4.x without this.
$env:CMAKE_POLICY_VERSION_MINIMUM = '3.5'

# Zig drives clang for C/C++ and lld for linking. A two-element CMAKE_<LANG>_COMPILER list makes CMake run
# "zig.exe cc"; the explicit target keeps the binary on the baseline x86-64 ISA rather than the build machine's
# CPU. CMake expects a single program for the archiver, so "zig ar"/"zig ranlib" run through cmd shims (Ninja
# always runs archive steps through cmd.exe on Windows, which is what makes a .cmd usable there).
$shims = Join-Path $cache 'shims'
New-Item -ItemType Directory -Force $shims | Out-Null
Set-Content -LiteralPath (Join-Path $shims 'zig-ar.cmd') -Value "@`"$zig`" ar %*" -Encoding ascii
Set-Content -LiteralPath (Join-Path $shims 'zig-ranlib.cmd') -Value "@`"$zig`" ranlib %*" -Encoding ascii
$ar = (Join-Path $shims 'zig-ar.cmd').Replace('\', '/')
$ranlib = (Join-Path $shims 'zig-ranlib.cmd').Replace('\', '/')
# @() keeps a one-element result an array: PowerShell unwraps it to a string otherwise, and splatting a string
# passes its characters as separate arguments ("Unknown argument -").
$parallel = @(if ($Jobs -gt 0) { '--parallel', "$Jobs" } else { '--parallel' })
$common = @('-G', 'Ninja', "-DCMAKE_MAKE_PROGRAM=$ninja", '-DCMAKE_BUILD_TYPE=Release',
    "-DCMAKE_C_COMPILER=$zig;cc", "-DCMAKE_CXX_COMPILER=$zig;c++",
    '-DCMAKE_C_FLAGS=-target x86_64-windows-gnu', '-DCMAKE_CXX_FLAGS=-target x86_64-windows-gnu',
    "-DCMAKE_AR=$ar", "-DCMAKE_RANLIB=$ranlib", "-DCMAKE_RC_COMPILER=$zig")

if (!$Perl) {
    $found = Get-Command perl -ErrorAction SilentlyContinue
    $Perl = if ($found) { $found.Source } else { Join-Path $env:ProgramFiles 'Git/usr/bin/perl.exe' }
}
if (!(Test-Path -LiteralPath $Perl)) { throw 'Perl is required to configure libaom; pass -Perl or install Git for Windows.' }

$prefix = Join-Path $cache 'prefix'
$aomBuild = Join-Path $cache 'build/aom'
& $CMake -S (Join-Path $sources 'libaom-3.14.1') -B $aomBuild @common "-DCMAKE_INSTALL_PREFIX=$prefix" "-DPERL_EXECUTABLE=$Perl" `
    '-DAOM_TARGET_CPU=generic' '-DCONFIG_RUNTIME_CPU_DETECT=0' '-DCONFIG_PIC=1' '-DBUILD_SHARED_LIBS=0' `
    '-DENABLE_DOCS=0' '-DENABLE_EXAMPLES=0' '-DENABLE_TESTDATA=0' '-DENABLE_TESTS=0' '-DENABLE_TOOLS=0'
Invoke-Checked 'libaom configure'
& $CMake --build $aomBuild @parallel
Invoke-Checked 'libaom build'
& $CMake --install $aomBuild
Invoke-Checked 'libaom install'

$avifBuild = Join-Path $cache 'build/avif'
& $CMake -S (Join-Path $sources 'libavif-1.4.2') -B $avifBuild @common "-DCMAKE_PREFIX_PATH=$prefix" `
    '-DBUILD_SHARED_LIBS=ON' '-DAVIF_CODEC_AOM=SYSTEM' '-DAVIF_CODEC_AOM_DECODE=ON' '-DAVIF_CODEC_AOM_ENCODE=ON' `
    '-DAVIF_CODEC_DAV1D=OFF' '-DAVIF_LIBYUV=OFF' '-DAVIF_LIBSHARPYUV=OFF' '-DAVIF_LIBXML2=OFF' `
    '-DAVIF_BUILD_APPS=OFF' '-DAVIF_BUILD_TESTS=OFF' '-DAVIF_ENABLE_WERROR=OFF'
Invoke-Checked 'libavif configure'
& $CMake --build $avifBuild @parallel
Invoke-Checked 'libavif build'

$built = Get-ChildItem -LiteralPath $avifBuild -Filter '*avif*.dll' -Recurse | Select-Object -First 1
if ($null -eq $built) { throw 'The libavif build did not produce a DLL.' }
$output = Join-Path $cache 'win-x64'
New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item -LiteralPath $built.FullName -Destination (Join-Path $output 'avif.dll') -Force
Copy-Item -LiteralPath (Join-Path $sources 'libavif-1.4.2/LICENSE') -Destination (Join-Path $output 'LICENSE.libavif.txt') -Force
Copy-Item -LiteralPath (Join-Path $sources 'libaom-3.14.1/LICENSE') -Destination (Join-Path $output 'LICENSE.libaom.txt') -Force
Copy-Item -LiteralPath (Join-Path $sources 'libaom-3.14.1/PATENTS') -Destination (Join-Path $output 'PATENTS.libaom.txt') -Force
Get-FileHash -LiteralPath (Join-Path $output 'avif.dll') -Algorithm SHA256
