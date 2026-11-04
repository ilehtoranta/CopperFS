param(
    [Parameter(Mandatory)][string]$Handler,
    [Parameter(Mandatory)][string]$ReferenceImage,
    [Parameter(Mandatory)][string]$NativeRedoImage,
    [Parameter(Mandatory)][string]$OriginalHandler,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Trst
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'tests.trx')) { throw 'Choose a fresh output directory for each qualification.' }
$settings = @{
    COPPERSFS_SFS2_HANDLER = [IO.Path]::GetFullPath($Handler)
    COPPERSFS_SFS2_REFERENCE_IMAGE = [IO.Path]::GetFullPath($ReferenceImage)
    COPPERSFS_SFS2_NATIVE_REDO_IMAGE = [IO.Path]::GetFullPath($NativeRedoImage)
    COPPERSFS_SFS2_ORIGINAL_HANDLER = [IO.Path]::GetFullPath($OriginalHandler)
    COPPERSFS_SFS2_NATIVE_EVIDENCE = $OutputDirectory
    COPPERSFS_SFS2_TRST_QUALIFICATION = if ($Trst) { '1' } else { $null }
}
$previous = @{}
try {
    foreach ($name in $settings.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $settings[$name])
    }
    foreach ($name in @('COPPERSFS_SFS2_HANDLER','COPPERSFS_SFS2_REFERENCE_IMAGE','COPPERSFS_SFS2_NATIVE_REDO_IMAGE','COPPERSFS_SFS2_ORIGINAL_HANDLER','COPPERSHARP_KICKSTART31_ROM')) {
        if (!(Test-Path -LiteralPath ([Environment]::GetEnvironmentVariable($name)))) { throw "Missing qualification input: $name" }
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $filter = if ($Trst) { 'FullyQualifiedName~NativeSfs2Tests' } else { 'FullyQualifiedName~NativeSfs2Tests&FullyQualifiedName!~NativeTrstArenaExhaustion' }
    dotnet test (Join-Path $repository 'tests/CopperSFS.Native.Tests') -c Release --artifacts-path (Join-Path $OutputDirectory 'build') --filter $filter --logger 'trx;LogFileName=tests.trx' --results-directory $OutputDirectory
    $testExitCode = $LASTEXITCODE
    [xml]$results = Get-Content -LiteralPath (Join-Path $OutputDirectory 'tests.trx') -Raw
    $tests = @($results.TestRun.Results.UnitTestResult)
    $covered = 0L; $groups = 0L
    foreach ($test in $tests) {
        $detail = [string]$test.Output.StdOut
        if ($detail -match 'SFS2 native .+: (\d+) sector/flush cuts, (\d+) actual cut/reboot groups') {
            $covered += [long]$Matches[1]; $groups += [long]$Matches[2]
        }
    }
    [ordered]@{
        handlerSha256 = (Get-FileHash -LiteralPath $Handler).Hash
        referenceSha256 = (Get-FileHash -LiteralPath $ReferenceImage).Hash
        originalRedoSha256 = (Get-FileHash -LiteralPath $NativeRedoImage).Hash
        originalHandlerSha256 = (Get-FileHash -LiteralPath $OriginalHandler).Hash
        trst = $Trst.IsPresent; testExitCode = $testExitCode; selected = $tests.Count
        passed = @($tests | Where-Object outcome -eq 'Passed').Count
        coveredSectorAndFlushCutPoints = $covered; actualCutRebootGroups = $groups
        boundary = 'Compiled MC68000 HUNK; 68000/68020 with licensed Kickstart 3.1, atomic sectors and reliable flushes. Distinct durable prefixes share a representative cut/reboot. Sparse wide-device checks inspect affected structures, not the entire allocation graph.'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
    if ($testExitCode -ne 0 -or !$tests.Count -or @($tests | Where-Object outcome -ne 'Passed').Count) {
        throw "Every selected native test must pass without skips: $OutputDirectory"
    }
}
finally { foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) } }
