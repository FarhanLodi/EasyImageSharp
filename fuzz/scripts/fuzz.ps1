<#
.SYNOPSIS
    Coverage-guided fuzzing of the EasyImageSharp decoders on Windows (libFuzzer through libfuzzer-dotnet).

.DESCRIPTION
    Publishes the harness, instruments EasyImageSharp.dll with SharpFuzz, builds the seed corpora and merged
    dictionaries from the test fixtures, then runs each target for a fixed time. The output layout matches
    fuzz/scripts/fuzz.sh: bin/, corpus/<target>/, work/<target>/, artifacts/<target>/, logs/<target>.log.

    libfuzzer-dotnet-windows.exe builds the child's command line by joining --target_path and --target_arg with a
    single space and no quoting, so the output directory must not contain spaces. The script stops early if it does;
    pass -Out with a space-free path (a short 8.3 path works).

.EXAMPLE
    pwsh fuzz/scripts/fuzz.ps1                         # every target, 60 s each
    pwsh fuzz/scripts/fuzz.ps1 -Seconds 600 png tiff   # two targets, ten minutes each
    pwsh fuzz/scripts/fuzz.ps1 -Seconds 300 jpeg -LibFuzzerArgs '-jobs=4','-workers=4'
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Targets = @(),
    [int]$Seconds = 60,
    [int]$Timeout = 25,
    [int]$MaxLen = 262144,
    [string]$Driver = $env:LIBFUZZER_DOTNET,
    [switch]$NoBuild,
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'out'),
    [string[]]$LibFuzzerArgs = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Pinned libfuzzer-dotnet release (MIT, https://github.com/Metalnem/libfuzzer-dotnet); update URL and digest together.
$DriverUrl = 'https://github.com/Metalnem/libfuzzer-dotnet/releases/download/v2025.05.02.0904/libfuzzer-dotnet-windows.exe'
$DriverSha256 = '17af5b3f6ff4d2c57b44b9a35c13051b570eb66f0557d00015df3832709050bf'
$SharpFuzzVersion = '2.3.0'

$fuzzDir = Split-Path -Parent $PSScriptRoot
$project = Join-Path $fuzzDir 'EasyImageSharp.Fuzz\EasyImageSharp.Fuzz.csproj'
New-Item -ItemType Directory -Force (Join-Path $Out 'tools') | Out-Null
$Out = (Resolve-Path $Out).Path
if ($Out.Contains(' ')) {
    throw "The output directory '$Out' contains a space, which libfuzzer-dotnet-windows.exe cannot pass to the harness. Use -Out with a space-free path."
}
$bin = Join-Path $Out 'bin'

# ----- Driver -----
if (-not $Driver) {
    $Driver = Join-Path $Out 'tools\libfuzzer-dotnet-windows.exe'
    $ok = (Test-Path $Driver) -and ((Get-FileHash $Driver -Algorithm SHA256).Hash -eq $DriverSha256.ToUpperInvariant())
    if (-not $ok) {
        Write-Host 'Downloading libfuzzer-dotnet-windows.exe...'
        $tmp = "$Driver.tmp"
        Invoke-WebRequest -UseBasicParsing -Uri $DriverUrl -OutFile $tmp
        $hash = (Get-FileHash $tmp -Algorithm SHA256).Hash
        if ($hash -ne $DriverSha256.ToUpperInvariant()) {
            Remove-Item $tmp
            throw "libfuzzer-dotnet-windows.exe has SHA-256 $hash, expected $DriverSha256."
        }
        Move-Item -Force $tmp $Driver
    }
}

# ----- Build and instrument -----
$harness = Join-Path $bin 'EasyImageSharp.Fuzz.exe'
if (-not $NoBuild) {
    $sharpfuzz = (Get-Command sharpfuzz -ErrorAction SilentlyContinue).Source
    if (-not $sharpfuzz) {
        $sharpfuzz = Join-Path $Out 'tools\sharpfuzz.exe'
        if (-not (Test-Path $sharpfuzz)) {
            dotnet tool install SharpFuzz.CommandLine --version $SharpFuzzVersion --tool-path (Join-Path $Out 'tools') | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Installing SharpFuzz.CommandLine failed.' }
        }
    }

    # A fresh directory every time: sharpfuzz rewrites EasyImageSharp.dll in place and must never see it twice.
    if (Test-Path $bin) { Remove-Item -Recurse -Force $bin }
    dotnet publish $project -c Release -o $bin --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    & $sharpfuzz (Join-Path $bin 'EasyImageSharp.dll')
    if ($LASTEXITCODE -ne 0) { throw 'sharpfuzz failed to instrument EasyImageSharp.dll.' }
}
elseif (-not (Test-Path $harness)) {
    throw "-NoBuild: $harness does not exist; run once without -NoBuild."
}

if ($Targets.Count -eq 0) {
    $Targets = & $harness list | ForEach-Object { ($_ -split '\s+')[0] }
}

# ----- Seeds and dictionaries -----
python (Join-Path $PSScriptRoot 'build_corpus.py') --out $Out --max-bytes $MaxLen --targets @Targets
if ($LASTEXITCODE -ne 0) { throw 'build_corpus.py failed.' }

# ----- Fuzz -----
$status = 0
$summary = @()
foreach ($target in $Targets) {
    $work = Join-Path $Out "work\$target"
    $artifacts = Join-Path $Out "artifacts\$target\"
    New-Item -ItemType Directory -Force $work, $artifacts, (Join-Path $Out 'logs') | Out-Null
    $log = Join-Path $Out "logs\$target.log"
    Write-Host "=== ${target}: ${Seconds}s"
    $arguments = @(
        "--target_path=$harness", "--target_arg=$target",
        "-dict=$(Join-Path $Out "dict\$target.dict")",
        "-max_total_time=$Seconds", "-timeout=$Timeout", "-max_len=$MaxLen",
        "-artifact_prefix=$artifacts", '-print_final_stats=1'
    ) + $LibFuzzerArgs + @($work, (Join-Path $Out "corpus\$target"))
    & $Driver @arguments *> $log
    $code = $LASTEXITCODE
    Get-Content $log -Tail 12
    $found = @(Get-ChildItem $artifacts -File | Where-Object { $_.Name -match '^(crash|timeout|oom)-' }).Count
    if ($code -ne 0 -or $found -gt 0) {
        $status = 1
        $summary += "${target}: FOUND $found reproducer(s), libFuzzer exit $code (see $log)"
    }
    else {
        $summary += "${target}: clean, corpus $(@(Get-ChildItem $work -File).Count) file(s)"
    }
}

''
$summary
exit $status
