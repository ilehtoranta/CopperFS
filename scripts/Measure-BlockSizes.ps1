param(
    [string]$OutputDirectory,
    [ValidateRange(3, 100)][int]$Repetitions = 5,
    [ValidateSet('all', 'benchmark', 'recovery')][string]$Mode = 'all'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repository ('artifacts/block-size/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
}
$reportPath = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'report.json'
if (Test-Path -LiteralPath $reportPath) { throw 'Choose a fresh output directory to preserve previous evidence.' }
$previousTieredCompilation = $env:DOTNET_TieredCompilation
Push-Location $repository
try {
    dotnet build tools/CopperSFS.Bench/CopperSFS.Bench.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
    $env:DOTNET_TieredCompilation = '0'
    dotnet tools/CopperSFS.Bench/bin/Release/net10.0/CopperSFS.Bench.dll --output $reportPath --repetitions $Repetitions --mode $Mode
    if ($LASTEXITCODE -ne 0) { throw "Block-size study failed. See $reportPath" }
}
finally {
    $env:DOTNET_TieredCompilation = $previousTieredCompilation
    Pop-Location
}
