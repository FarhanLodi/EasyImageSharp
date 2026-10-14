$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$tool = Join-Path $workspace '.artifacts/native/lcms2/win-x64/transicc.exe'
if (!(Test-Path -LiteralPath $tool)) { throw 'Run build-littlecms.ps1 -BuildReferenceTool first.' }
$fixtures = Join-Path $workspace 'tests/EasyImageSharp.LittleCms.Tests/Fixtures'
$records = [Collections.Generic.List[object]]::new()

function AddCases([string]$source, [string]$destination, [int]$intent, [bool]$bpc, [string[]]$samples) {
    $inputProfile = if ($source -eq 'srgb') { '*sRGB' } else { Join-Path $fixtures $source }
    $outputProfile = if ($destination -eq 'srgb') { '*sRGB' } else { Join-Path $fixtures $destination }
    $arguments = @('-n', "-t$intent", "-i$inputProfile", "-o$outputProfile")
    if ($bpc) { $arguments += '-b' }
    $lines = @((@($samples) + 'q') | & $tool @arguments)
    if ($LASTEXITCODE -ne 0) { throw 'transicc failed.' }
    if ($lines.Count -ne $samples.Count) { throw 'Unexpected transicc output count.' }
    for ($i = 0; $i -lt $samples.Count; $i++) {
        $inputValues = @($samples[$i].Trim() -split '\s+' | ForEach-Object { [float]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
        $outputValues = @($lines[$i].Trim() -split '\s+' | ForEach-Object { [float]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
        if ($source -ne 'test1.icc') { $inputValues = @($inputValues | ForEach-Object { $_ / 255.0 }) }
        if ($destination -ne 'test1.icc') { $outputValues = @($outputValues | ForEach-Object { $_ / 255.0 }) }
        $records.Add([ordered]@{ source = $source; destination = $destination; intent = $intent; blackPointCompensation = $bpc; input = $inputValues; expected = $outputValues })
    }
}

AddCases 'test5.icc' 'srgb' 1 $false @('0 0 0', '255 255 255', '64 128 192', '255 0 0')
foreach ($intent in 0..3) {
    foreach ($bpc in @($false, $true)) {
        AddCases 'test1.icc' 'srgb' $intent $bpc @('0 0 0 0', '10 25 50 5', '100 0 0 0', '0 0 0 100')
        AddCases 'srgb' 'test1.icc' $intent $bpc @('32 64 128', '0 0 0', '255 0 0')
        AddCases 'test3.icc' 'srgb' $intent $bpc @('32 64 128', '0 0 0', '255 255 255')
    }
}
$json = ConvertTo-Json -InputObject $records.ToArray() -Depth 5
[IO.File]::WriteAllText((Join-Path $fixtures 'transicc-reference.json'), $json)
