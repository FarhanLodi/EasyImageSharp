<#
.SYNOPSIS
    Builds the documentation site with DocFX.

.DESCRIPTION
    The site reuses the repository's own Markdown (README.md, docs/*.md, the package and tool READMEs,
    CHANGELOG.md and the policy files) instead of keeping copies of it. DocFX cannot build content that
    lives above the folder holding docfx.json, so this script first copies those files into the
    gitignored docs/site/_repo folder with their relative paths intact, which keeps every link between
    them working, and then runs DocFX from the local tool manifest (.config/dotnet-tools.json).

    Works on Windows PowerShell 5.1 and PowerShell 7 on any OS. The output is docs/site/_site.

.PARAMETER Serve
    Serve the built site on http://localhost:8080 after building.

.PARAMETER WarningsAsErrors
    Fail the build on any DocFX warning.

.EXAMPLE
    ./docs/site/build.ps1 -Serve
#>
param(
    [switch]$Serve,
    [switch]$WarningsAsErrors
)

$ErrorActionPreference = 'Stop'
$site = $PSScriptRoot
$root = Split-Path (Split-Path $site -Parent) -Parent
$stage = Join-Path $site '_repo'

# Repository files rendered on the site, relative to the repository root. Wildcards match within one
# directory only, so docs/*.md does not pick up anything under docs/site. The JSON files are the
# measurement records the benchmark and tool READMEs link to; they are published as-is so those links
# resolve on the site too.
$patterns = @(
    'README.md',
    'CHANGELOG.md',
    'CONTRIBUTING.md',
    'CODE_OF_CONDUCT.md',
    'SECURITY.md',
    'SUPPORT.md',
    'docs/*.md',
    'src/*/README.md',
    'src/EasyImageSharp/PublicAPI/README.md',
    'src/EasyImageSharp/Assets/icon.png',
    'tools/*/README.md',
    'benchmarks/*.md',
    'benchmarks/*.json',
    'benchmarks/results/*.md',
    'benchmarks/results/*.json',
    'benchmarks/ocr/*.md',
    'benchmarks/ocr/results/*.json',
    'benchmarks/resource-isolation/*.json'
)

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}

$copied = 0
foreach ($pattern in $patterns) {
    $found = @(Resolve-Path -Path (Join-Path $root $pattern) -ErrorAction SilentlyContinue)
    if ($found.Count -eq 0) {
        throw "No repository file matches '$pattern'. Update the list in docs/site/build.ps1."
    }

    foreach ($match in $found) {
        $source = $match.ProviderPath
        $relative = $source.Substring($root.Length).TrimStart('\', '/')
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
        $copied++
    }
}
Write-Host "Staged $copied repository files into $stage"

Push-Location $root
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

    $arguments = @('docfx', (Join-Path $site 'docfx.json'))
    if ($WarningsAsErrors) { $arguments += '--warningsAsErrors' }
    if ($Serve) { $arguments += '--serve' }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "DocFX failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
