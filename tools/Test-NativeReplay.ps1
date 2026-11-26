<#
.SYNOPSIS
Builds fresh standalone handlers and runs CopperStart's native sparse-storage replay.

.DESCRIPTION
Produces the COPPERSFS_SPARSE_ARTIFACTS layout that
CopperStart.Sfs.Native.Tests expects (classic, ffs, sfs2-csun, sfs2-trst,
command), records a SHA-256 manifest of every binary, and optionally runs the
twelve NativeSparseStorageTests cases against those binaries with a TRX log.

The licensed Kickstart 3.1 ROM is read from -Rom or COPPERSHARP_KICKSTART31_ROM
and is never copied. Output stays under the ignored artifacts/ directory unless
-ArtifactRoot names another location. Skipped cases are reported as skipped,
never as passes.
#>
param(
    [string]$ArtifactRoot = '',
    [string]$CopperStartRoot = '',
    [string]$CopperSharpRoot = '',
    [string]$Rom = $env:COPPERSHARP_KICKSTART31_ROM,
    [string]$Filter = 'FullyQualifiedName~NativeSparseStorageTests',
    [switch]$SkipBuild,
    # Reuse the already built CopperSharp compiler, for example while another
    # process holds its output files.
    [switch]$SkipCompilerBuild,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $CopperStartRoot) { $CopperStartRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '../CopperStart')) }
if (-not $ArtifactRoot) { $ArtifactRoot = Join-Path $repositoryRoot ('artifacts/native-replay-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
$common = @{}
if ($CopperSharpRoot) { $common.CopperSharpRoot = $CopperSharpRoot }

# Each entry is the subdirectory name CopperStart's replay expects and the
# Build-Handler.ps1 switches documented in README.md for that handler.
$handlers = [ordered]@{
    'classic'   = @{}
    'ffs'       = @{ Ffs = $true }
    'sfs2-csun' = @{ Sfs2 = $true; Sfs2CsunWrites = $true }
    'sfs2-trst' = @{ Sfs2 = $true }
}

if (-not $SkipBuild) {
    $first = -not $SkipCompilerBuild
    foreach ($name in $handlers.Keys) {
        $arguments = @{} + $common + $handlers[$name]
        $arguments.ArtifactRoot = Join-Path $ArtifactRoot $name
        if (-not $first) { $arguments.SkipCompilerBuild = $true }
        & (Join-Path $repositoryRoot 'build/Build-Handler.ps1') @arguments
        $first = $false
    }
    $trim = @{} + $common
    $trim.ArtifactRoot = Join-Path $ArtifactRoot 'command'
    $trim.SkipCompilerBuild = $true
    & (Join-Path $repositoryRoot 'build/Build-Trim.ps1') @trim
}

$binaries = @(
    'classic/CopperSFS', 'ffs/CopperFFS', 'sfs2-csun/CopperSFS', 'sfs2-trst/CopperSFS',
    'command/CopperTrim', 'command/CopperSFSTrim')
$manifest = [ordered]@{
    Source = (git -C $repositoryRoot rev-parse HEAD)
    SourceDirty = [bool](git -C $repositoryRoot status --porcelain)
    CopperStart = (git -C $CopperStartRoot rev-parse HEAD)
    CopperStartDirty = [bool](git -C $CopperStartRoot status --porcelain)
    Binaries = [ordered]@{}
}
foreach ($relative in $binaries) {
    $path = Join-Path $ArtifactRoot $relative
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing replay binary: $path" }
    $manifest.Binaries[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'replay-manifest.json') -Encoding utf8
Write-Host "Replay artifacts: $ArtifactRoot"

if (-not $Run) {
    Write-Host "Run: `$env:COPPERSFS_SPARSE_ARTIFACTS = '$ArtifactRoot'; dotnet test '$CopperStartRoot/tests/CopperStart.Sfs.Native.Tests' -c Release --filter '$Filter'"
    return
}
if (-not $Rom -or -not (Test-Path -LiteralPath $Rom)) { throw 'Set -Rom or COPPERSHARP_KICKSTART31_ROM to a licensed Kickstart 3.1 ROM.' }

$env:COPPERSHARP_KICKSTART31_ROM = $Rom
$env:COPPERSFS_SPARSE_ARTIFACTS = $ArtifactRoot
$results = Join-Path $ArtifactRoot 'results'
dotnet test (Join-Path $CopperStartRoot 'tests/CopperStart.Sfs.Native.Tests') -c Release --filter $Filter `
    --logger 'trx;LogFileName=native-replay.trx' --results-directory $results
$exit = $LASTEXITCODE

$trx = Join-Path $results 'native-replay.trx'
if (Test-Path -LiteralPath $trx) {
    [xml]$report = Get-Content -LiteralPath $trx
    $counters = $report.TestRun.ResultSummary.Counters
    Write-Host ("Native replay: total {0}, passed {1}, failed {2}, not executed {3}" -f
        $counters.total, $counters.passed, $counters.failed, $counters.notExecuted)
}
exit $exit
