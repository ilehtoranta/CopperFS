param([string]$OutputDirectory, [string]$ArtifactsPath)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repository ('artifacts/reliability/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'tests.trx')) {
    throw 'Choose a fresh output directory so results from different runs cannot be mixed.'
}
$previousArtifacts = $env:COPPERSFS_RELIABILITY_ARTIFACTS
$env:COPPERSFS_RELIABILITY_ARTIFACTS = Join-Path $OutputDirectory 'failures'
Push-Location $repository
try {
    # Include uncommitted and untracked sources: the commit alone may not identify
    # the code tested. Hash before running so concurrent changes are detectable.
    $files = @(git ls-files --cached --others --exclude-standard)
    $sourceHashes = @($files | Sort-Object -Unique | ForEach-Object {
        [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    })
    $revision = git rev-parse HEAD
    $sdk = dotnet --version
    $testArguments = @('test', 'CopperSFS.slnx', '-c', 'Release', '--nologo', '--logger',
        'trx;LogFileName=tests.trx', '--results-directory', $OutputDirectory)
    if ($ArtifactsPath) { $testArguments += @('--artifacts-path', [IO.Path]::GetFullPath($ArtifactsPath)) }
    dotnet @testArguments
    $testExitCode = $LASTEXITCODE
    $trxPath = Join-Path $OutputDirectory 'tests.trx'
    $counters = $null; $evidence = @()
    if (Test-Path -LiteralPath $trxPath) {
        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $counters = $trx.TestRun.ResultSummary.Counters
        $evidence = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.Output.StdOut } | ForEach-Object {
            [ordered]@{ test = $_.testName; outcome = $_.outcome; detail = [string]$_.Output.StdOut }
        })
    }
    $binaryDirectory = if ($ArtifactsPath) {
        Join-Path ([IO.Path]::GetFullPath($ArtifactsPath)) 'bin/CopperSFS.Core.Tests/release'
    } else { Join-Path $repository 'tests/CopperSFS.Core.Tests/bin/Release/net10.0' }
    $binaries = @(Get-ChildItem -LiteralPath $binaryDirectory -Filter '*.dll' |
        Sort-Object Name | ForEach-Object { $_.FullName })
    $binaryHashes = @($binaries | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object {
        [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    })
    $changed = @($sourceHashes | Where-Object { !(Test-Path -LiteralPath $_.path) -or
        (Get-FileHash -LiteralPath $_.path -Algorithm SHA256).Hash -ne $_.sha256 })
    $fileListChanged = @(Compare-Object $files @(git ls-files --cached --others --exclude-standard)).Count -gt 0
    $mutationImages = 0; $recoveryImages = 0; $tornRecovered = 0; $tornRefused = 0
    $batched = [ordered]@{
        exhaustiveMutationAndFailureImages = 0; exhaustiveRecoveryImages = 0
        sampledMutationImages = 0; sampledRecoveryImages = 0
        tornSectorRecovered = 0; tornSectorRefusedMount = 0
    }
    $relocation = [ordered]@{ exhaustiveImages = 0; sampledImages = 0; tornRecovered = 0; tornRefusedMount = 0 }
    $sfs2 = [ordered]@{ exhaustiveMutationImages = 0; exhaustiveRecoveryImages = 0;
        exhaustiveWideImages = 0; exhaustiveMetadataImages = 0; exhaustiveBatchImages = 0;
        sampledBatchImages = 0; sampledBatchFailureImages = 0; tornRecovered = 0; tornRefusedMount = 0 }
    foreach ($entry in $evidence) {
        if ($entry.detail -match 'SFS2 .+: (\d+) exhaustive mutation crash/failure image checks') {
            $sfs2.exhaustiveMutationImages += [long]$Matches[1]
        }
        if ($entry.detail -match 'SFS2 recovery .+: (\d+) exhaustive recovery image checks; (\d+) tears recovered, (\d+) refused mount') {
            $sfs2.exhaustiveRecoveryImages += [long]$Matches[1]
            $sfs2.tornRecovered += [long]$Matches[2]; $sfs2.tornRefusedMount += [long]$Matches[3]
        }
        if ($entry.detail -match 'SFS2 wide .+: (\d+) exhaustive >4 GiB write/recovery images') {
            $sfs2.exhaustiveWideImages += [long]$Matches[1]
        }
        if ($entry.detail -match 'SFS2 metadata .+: (\d+) exhaustive image checks') {
            $sfs2.exhaustiveMetadataImages += [long]$Matches[1]
        }
        if ($entry.detail -match 'SFS2 batches .+: (\d+) exhaustive image checks; (\d+) sampled image checks') {
            $sfs2.exhaustiveBatchImages += [long]$Matches[1]; $sfs2.sampledBatchImages += [long]$Matches[2]
        }
        if ($entry.detail -match 'SFS2 batch failures: (\d+) sampled image checks') {
            $sfs2.sampledBatchFailureImages += [long]$Matches[1]
        }
        if ($entry.detail -match '(\d+) mutation crash images, (\d+) recovery crash images') {
            $mutationImages += [int]$Matches[1]; $recoveryImages += [int]$Matches[2]
        }
        if ($entry.detail -match '(\d+) torn-write cases recovered, (\d+) refused mount') {
            $tornRecovered += [int]$Matches[1]; $tornRefused += [int]$Matches[2]
        }
        if ($entry.detail -match 'Batched exhaustive: (\d+) mutation/failure images, (\d+) recovery images') {
            $batched.exhaustiveMutationAndFailureImages += [int]$Matches[1]
            $batched.exhaustiveRecoveryImages += [int]$Matches[2]
        }
        if ($entry.detail -match 'Batched (?:near-full|fragmented journal): (\d+) exhaustive image checks') {
            $batched.exhaustiveMutationAndFailureImages += [int]$Matches[1]
        }
        if ($entry.detail -match 'Batched sampled: (\d+) mutation images, (\d+) recovery images') {
            $batched.sampledMutationImages += [int]$Matches[1]
            $batched.sampledRecoveryImages += [int]$Matches[2]
        }
        if ($entry.detail -match 'Batched torn sectors: (\d+) recovered, (\d+) refused mount') {
            $batched.tornSectorRecovered += [int]$Matches[1]
            $batched.tornSectorRefusedMount += [int]$Matches[2]
        }
        if ($entry.detail -match 'Relocation: (\d+) exhaustive image checks, (\d+) sampled image checks') {
            $relocation.exhaustiveImages += [int]$Matches[1]
            $relocation.sampledImages += [int]$Matches[2]
        }
        if ($entry.detail -match 'Relocation tears: (\d+) recovered, (\d+) refused mount') {
            $relocation.tornRecovered += [int]$Matches[1]
            $relocation.tornRefusedMount += [int]$Matches[2]
        }
    }
    $result = [ordered]@{
        utc = [DateTime]::UtcNow.ToString('o'); commit = [string]$revision; sdk = [string]$sdk
        testExitCode = $testExitCode; sourceChangedDuringRun = $changed.Count -gt 0 -or $fileListChanged
        total = if ($counters) { [int]$counters.total } else { 0 }
        passed = if ($counters) { [int]$counters.passed } else { 0 }
        failed = if ($counters) { [int]$counters.failed } else { 0 }
        mutationCrashImages = $mutationImages; recoveryCrashImages = $recoveryImages
        tornWriteRecovered = $tornRecovered; tornWriteRefusedMount = $tornRefused
        batchedWrites = $batched
        relocation = $relocation
        sfs2 = $sfs2
        maxCrashStates = if ($env:COPPERSFS_MAX_CRASH_STATES) { $env:COPPERSFS_MAX_CRASH_STATES } else { '16777216' }
        sourceHashes = $sourceHashes; binaryHashes = $binaryHashes; evidence = $evidence
        boundary = 'Portable-core host reliability tests. Optional original-handler ROM handoffs retain separate emulator evidence; no rebuilt native CopperSFS handler or physical-device qualification.'
    }
    $reportPath = Join-Path $OutputDirectory 'report.json'
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "Reliability evidence: $reportPath"
    if ($testExitCode -ne 0) { throw "Tests failed with exit code $testExitCode. See $trxPath" }
    if ($changed.Count -or $fileListChanged) { throw 'Source files changed during testing; rerun for valid evidence.' }
}
finally {
    Pop-Location
    $env:COPPERSFS_RELIABILITY_ARTIFACTS = $previousArtifacts
}
